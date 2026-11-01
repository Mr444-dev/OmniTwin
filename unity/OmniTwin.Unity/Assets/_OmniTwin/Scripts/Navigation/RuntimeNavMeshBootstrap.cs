using System;
using System.Collections.Generic;
using OmniTwin.Simulation.Agents;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace OmniTwin.Simulation.Navigation
{
    /// <summary>Builds the tabletop walkable mesh before ground-agent Awake callbacks in Editor and Player startup.</summary>
    internal static class RuntimeNavMeshBootstrap
    {
        private const string WorkspaceName = "Reference Workspace 20 x 12.5 m";
        private static NavMeshData _runtimeData;
        private static NavMeshDataInstance _instance;
        private static Scene _workspaceScene;
        private static bool _attempted;

        /// <summary>Registers a runtime floor mesh early enough for every serialized NavMeshAgent to initialize safely.</summary>
        internal static void EnsureBeforeAgentsAwake()
        {
            if (_attempted) return;
            _attempted = true;
            if (NavMesh.CalculateTriangulation().vertices.Length > 0) return;

            try
            {
                var surfaces = UnityEngine.Object.FindObjectsByType<NavMeshSurface>(FindObjectsInactive.Include);
                NavMeshSurface surface = null;
                for (var i = 0; i < surfaces.Length; i++)
                {
                    if (surfaces[i] != null && surfaces[i].gameObject.name == WorkspaceName)
                    {
                        surface = surfaces[i];
                        break;
                    }
                }

                if (surface == null) throw new InvalidOperationException("The tactical workspace NavMeshSurface is missing.");
                if (!surface.TryGetComponent<BoxCollider>(out var floor))
                    throw new InvalidOperationException("The tactical workspace BoxCollider is missing.");

                var settings = surface.GetBuildSettings();
                if (settings.agentTypeID < 0 || settings.agentHeight <= 0 || settings.agentRadius <= 0)
                    throw new InvalidOperationException("The ground-agent NavMesh build settings are invalid.");
                var walkableArea = NavMesh.GetAreaFromName("Walkable");
                if (walkableArea < 0) throw new InvalidOperationException("Unity's Walkable NavMesh area is unavailable.");

                var sources = new List<NavMeshBuildSource>(1)
                {
                    new NavMeshBuildSource
                    {
                        shape = NavMeshBuildSourceShape.Box,
                        transform = floor.transform.localToWorldMatrix * Matrix4x4.Translate(floor.center),
                        size = floor.size,
                        area = walkableArea
                    }
                };
                var worldBounds = TransformBounds(new Bounds(floor.center, floor.size), floor.transform.localToWorldMatrix);
                worldBounds.Expand(new Vector3(0f, Math.Max(4f, settings.agentHeight * 2f + 2f), 0f));
                var toBuildSpace = Matrix4x4.Rotate(Quaternion.Inverse(surface.transform.rotation)) *
                    Matrix4x4.Translate(-surface.transform.position);
                var localBounds = TransformBounds(worldBounds, toBuildSpace);
                _runtimeData = NavMeshBuilder.BuildNavMeshData(settings, sources, localBounds,
                    surface.transform.position, surface.transform.rotation);
                if (_runtimeData == null) throw new InvalidOperationException("Unity could not build NavMeshData from the tabletop floor collider.");

                _runtimeData.name = "OmniTwin Runtime Tactical Floor";
                surface.navMeshData = null;
                _instance = NavMesh.AddNavMeshData(_runtimeData, surface.transform.position, surface.transform.rotation);
                var vertexCount = NavMesh.CalculateTriangulation().vertices.Length;
                var samplePoint = floor.transform.TransformPoint(floor.center) + Vector3.up * (floor.size.y * Mathf.Abs(floor.transform.lossyScale.y) * 0.5f);
                if (!_instance.valid || vertexCount < 3 ||
                    !NavMesh.SamplePosition(samplePoint, out _, 0.5f, NavMesh.AllAreas))
                    throw new InvalidOperationException("The runtime tabletop NavMesh did not register any walkable polygons.");

                _workspaceScene = surface.gameObject.scene;
                SceneManager.sceneUnloaded += OnSceneUnloaded;
                Debug.Log("OmniTwin: runtime tactical NavMesh registered before agent startup (" + vertexCount + " vertices).");
            }
            catch (Exception error)
            {
                Debug.LogError("OmniTwin: runtime tactical NavMesh initialization failed: " + error.Message);
            }
        }

        private static Bounds TransformBounds(Bounds source, Matrix4x4 matrix)
        {
            var minimum = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            var maximum = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            var center = source.center;
            var extents = source.extents;
            for (var x = -1; x <= 1; x += 2)
            for (var y = -1; y <= 1; y += 2)
            for (var z = -1; z <= 1; z += 2)
            {
                var corner = center + Vector3.Scale(extents, new Vector3(x, y, z));
                var transformed = matrix.MultiplyPoint3x4(corner);
                minimum = Vector3.Min(minimum, transformed);
                maximum = Vector3.Max(maximum, transformed);
            }
            return new Bounds((minimum + maximum) * 0.5f, maximum - minimum);
        }

        private static void OnSceneUnloaded(Scene scene)
        {
            if (!scene.Equals(_workspaceScene)) return;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            if (_instance.valid) _instance.Remove();
            if (_runtimeData != null) UnityEngine.Object.Destroy(_runtimeData);
            _instance = new NavMeshDataInstance();
            _runtimeData = null;
            _workspaceScene = default;
            _attempted = false;
        }
    }
}
