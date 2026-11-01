using System;
using System.Collections.Generic;
using OmniTwin.Contracts;
using OmniTwin.Simulation.Network;
using OmniTwin.Transport;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;

namespace OmniTwin.Simulation.World
{
    /// <summary>Displays actual tracked circular footprints as configurable height proxies on the X/Z plane.</summary>
    [DisallowMultipleComponent]
    public sealed class DynamicObstacleSpawner : MonoBehaviour
    {
        [SerializeField] private TwinWebSocketClient stream = null;
        [SerializeField, Min(0.05f)] private float proxyHeightMeters = 1;
        [SerializeField, Min(0.1f)] private float interpolationRate = 12;
        [SerializeField] private Color obstacleColor = new Color(0.1f, 0.8f, 1, 1);
        [SerializeField] private Color hazardColor = new Color(1, 0.2f, 0.1f, 1);
        [SerializeField] private Material hologramMaterial;
        [SerializeField] private Material accentMaterial;
        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        private readonly Dictionary<int, Visual> _visuals = new Dictionary<int, Visual>();
        private readonly HashSet<int> _seen = new HashSet<int>();
        private readonly List<int> _removed = new List<int>();
        private WorldStreamMessage _last;
        private Guid _streamId;
        private Guid _sessionId;
        private Guid _calibrationId;

        /// <summary>Gets whether the last frame could display current, validated observations.</summary>
        public bool IsWorldCurrent { get; private set; }

        private void OnEnable()
        {
            if (stream == null || !Finite(proxyHeightMeters) || proxyHeightMeters < 0.05f ||
                !Finite(interpolationRate) || interpolationRate < 0.1f)
            {
                Debug.LogError("OmniTwin: assign the stream and finite positive proxy/interpolation settings.", this);
                enabled = false;
            }
        }

        private void Update()
        {
            WorldStreamClientView view;
            if (stream == null || !stream.TryGetCurrentView(out view))
            {
                IsWorldCurrent = false;
                foreach (var visual in _visuals.Values) visual.Root.SetActive(false);
                _last = null;
                return;
            }
            IsWorldCurrent = true;
            var message = view.State.Message;
            var changed = !ReferenceEquals(message, _last);
            if (changed)
            {
                if (message.StreamId != _streamId || message.World.SessionId != _sessionId || message.World.CalibrationId != _calibrationId)
                {
                    Clear();
                    _streamId = message.StreamId; _sessionId = message.World.SessionId; _calibrationId = message.World.CalibrationId;
                }
            }
            _seen.Clear();
            foreach (var track in message.World.Obstacles)
            {
                if (!view.IsTrackCurrent(track)) continue;
                var obstacle = track.Obstacle;
                _seen.Add(obstacle.Id);
                if (!_visuals.TryGetValue(obstacle.Id, out var visual))
                {
                    visual = CreateVisual(track);
                    _visuals.Add(obstacle.Id, visual);
                }
                if (!changed) continue;
                visual.Root.SetActive(true);
                visual.Target = new Vector3(obstacle.WorldPosition.X, 0, obstacle.WorldPosition.Z);
                visual.Body.localScale = new Vector3(2 * obstacle.RadiusMeters, proxyHeightMeters / 2, 2 * obstacle.RadiusMeters);
                visual.Obstacle.radius = obstacle.RadiusMeters;
                visual.Obstacle.carving = track.Lifecycle == ObstacleLifecycleState.SettledObstacle;
                visual.Tint.SetColor(BaseColor, CoreColor(track));
                visual.Renderer.SetPropertyBlock(visual.Tint);
                var label = FormatCallout(track);
                visual.Callout.UpdateDetails(label, RenderColor(track), obstacle.RadiusMeters, proxyHeightMeters);
            }
            _removed.Clear();
            foreach (var id in _visuals.Keys) if (!_seen.Contains(id)) _removed.Add(id);
            foreach (var id in _removed) { Destroy(_visuals[id].Root); _visuals.Remove(id); }
            _last = message;
            var blend = 1 - Mathf.Exp(-interpolationRate * Time.unscaledDeltaTime);
            foreach (var visual in _visuals.Values)
                visual.Root.transform.position = Vector3.Lerp(visual.Root.transform.position, visual.Target, blend);
        }

        private Visual CreateVisual(TrackedObstacleState track)
        {
            var obstacle = track.Obstacle;
            var root = new GameObject("Obstacle " + obstacle.Id);
            root.transform.SetParent(transform, true);
            root.transform.position = new Vector3(obstacle.WorldPosition.X, 0, obstacle.WorldPosition.Z);
            var body = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            body.name = "Observed footprint proxy";
            body.transform.SetParent(root.transform, false);
            body.transform.localPosition = new Vector3(0, proxyHeightMeters / 2, 0);
            var renderer = body.GetComponent<Renderer>();
            var pipeline = GraphicsSettings.currentRenderPipeline;
            if (hologramMaterial != null) renderer.sharedMaterial = hologramMaterial;
            else if (pipeline != null && pipeline.defaultMaterial != null) renderer.sharedMaterial = pipeline.defaultMaterial;
            var tint = new MaterialPropertyBlock(); tint.SetColor(BaseColor, CoreColor(track)); renderer.SetPropertyBlock(tint);
            var nav = root.AddComponent<NavMeshObstacle>();
            nav.shape = NavMeshObstacleShape.Capsule;
            nav.center = new Vector3(0, proxyHeightMeters / 2, 0);
            nav.height = proxyHeightMeters;
            nav.radius = obstacle.RadiusMeters;
            nav.carving = track.Lifecycle == ObstacleLifecycleState.SettledObstacle;
            nav.carveOnlyStationary = true;
            nav.carvingMoveThreshold = 0.05f;
            nav.carvingTimeToStationary = 0.2f;
            var callout = root.AddComponent<WorldSpaceTacticalCallout>();
            var accent = accentMaterial != null ? accentMaterial : renderer.sharedMaterial;
            var label = FormatCallout(track);
            callout.Configure(accent, label, RenderColor(track), obstacle.RadiusMeters, proxyHeightMeters, obstacle.Id);
            return new Visual(root, body.transform, renderer, nav, callout);
        }

        private Color RenderColor(TrackedObstacleState track)
        {
            if (track.Obstacle.IsHazardZone) return hazardColor;
            if (track.ObservationSource == ObservationSource.LocalMotionEstimate) return new Color(0.92f, 0.66f, 0.24f, 0.85f);
            if (track.ObservationSource == ObservationSource.LocalImageSegmentation) return new Color(0.25f, 0.86f, 0.7f, 0.88f);
            return obstacleColor;
        }

        private static Color CoreColor(TrackedObstacleState track)
        {
            return track.Obstacle.IsHazardZone
                ? new Color(0.16f, 0.035f, 0.025f, 0.20f)
                : new Color(0.025f, 0.105f, 0.14f, 0.20f);
        }

        private static string FormatCallout(TrackedObstacleState track)
        {
            var id = track.Obstacle.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return track.Obstacle.IsHazardZone
                ? "TRACK #" + id + " · HAZARD ZONE"
                : "TRACK #" + id + " · OBSTACLE (" + track.Obstacle.Confidence.ToString("P0", System.Globalization.CultureInfo.InvariantCulture) + ")";
        }

        private void OnDisable() { IsWorldCurrent = false; Clear(); }
        private void Clear()
        {
            foreach (var visual in _visuals.Values) Destroy(visual.Root);
            _visuals.Clear(); _last = null;
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private sealed class Visual
        {
            internal Visual(GameObject root, Transform body, Renderer renderer, NavMeshObstacle obstacle, WorldSpaceTacticalCallout callout)
            { Root = root; Body = body; Renderer = renderer; Obstacle = obstacle; Callout = callout; Target = root.transform.position; }
            internal readonly GameObject Root;
            internal readonly Transform Body;
            internal readonly Renderer Renderer;
            internal readonly NavMeshObstacle Obstacle;
            internal readonly WorldSpaceTacticalCallout Callout;
            internal readonly MaterialPropertyBlock Tint = new MaterialPropertyBlock();
            internal Vector3 Target;
        }
    }
}
