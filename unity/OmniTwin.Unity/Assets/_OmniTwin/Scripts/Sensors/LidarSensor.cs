using System;
using OmniTwin.Contracts;
using OmniTwin.Simulation.Agents;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;

namespace OmniTwin.Simulation.Sensors
{
    /// <summary>Multi-beam planar raycast sensor. Visibility never disables sensing or the independent swept-body guard.</summary>
    [DisallowMultipleComponent, DefaultExecutionOrder(-80)]
    public sealed class LidarSensor : MonoBehaviour
    {
        [SerializeField, Range(8, 128)] private int beamCount = 48;
        [SerializeField, Range(30, 360)] private float fieldOfView = 360;
        [SerializeField, Range(0.5f, 30)] private float rangeMeters = 6;
        [SerializeField, Range(5, 50)] private float scansPerSecond = 20;
        [SerializeField] private LayerMask collisionMask = ~0;
        private readonly RaycastHit[] _hits = new RaycastHit[128];
        private readonly Collider[] _overlaps = new Collider[128];
        private Vector3[] _directions, _ends, _rayVertices, _hitVertices;
        private float[] _distances;
        private int[] _rayIndices, _hitIndices;
        private int _hitCount;
        private float _sampledAt = -1, _nextScan, _minimum;
        private bool _visible;
        private SwarmAgentController _owner;
        private Mesh _rays, _markers;
        private MeshRenderer _rayRenderer, _markerRenderer;
        private Material _rayMaterial, _hitMaterial;

        /// <summary>Gets whether beam rendering is enabled; scanning continues while hidden.</summary>
        public bool VisualizationEnabled => _visible;
        /// <summary>Gets configured beam count.</summary>
        public int BeamCount => beamCount;
        /// <summary>Gets the last actual scan summary, independently aged.</summary>
        public LidarTelemetry ReadTelemetry() => _sampledAt < 0 ? null : new LidarTelemetry(beamCount, _hitCount, rangeMeters,
            _hitCount == 0 ? (float?)null : _minimum, Mathf.Clamp(Mathf.RoundToInt((Time.unscaledTime - _sampledAt) * 1000), 0, 60000));

        private void Awake()
        {
            if (beamCount < 8 || beamCount > 128 || !Finite(fieldOfView) || fieldOfView < 30 || fieldOfView > 360 ||
                !Finite(rangeMeters) || rangeMeters < 0.5f || rangeMeters > 30 || !Finite(scansPerSecond) || scansPerSecond < 5 || scansPerSecond > 50)
                throw new InvalidOperationException("Invalid LiDAR configuration.");
            _owner = GetComponent<SwarmAgentController>();
            if (_owner == null) throw new InvalidOperationException("LiDAR requires its owning agent.");
            _directions = new Vector3[beamCount]; _ends = new Vector3[beamCount]; _distances = new float[beamCount];
            _rayVertices = new Vector3[beamCount * 2]; _hitVertices = new Vector3[beamCount * 6];
            _rayIndices = new int[_rayVertices.Length]; _hitIndices = new int[_hitVertices.Length];
            for (var i = 0; i < _rayIndices.Length; i++) _rayIndices[i] = i;
            for (var i = 0; i < _hitIndices.Length; i++) _hitIndices[i] = i;
            _rays = Visual("LiDAR beams", new Color(0.04f, 0.42f, 0.48f), out _rayRenderer, out _rayMaterial);
            _markers = Visual("LiDAR hit markers", new Color(1, 0.38f, 0.08f), out _markerRenderer, out _hitMaterial);
            SetVisualizationEnabled(false);
        }
        private Mesh Visual(string name, Color color, out MeshRenderer renderer, out Material material)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) throw new InvalidOperationException("URP Unlit is required for LiDAR.");
            var child = new GameObject(name); child.transform.SetParent(transform, false);
            var filter = child.AddComponent<MeshFilter>(); renderer = child.AddComponent<MeshRenderer>();
            material = new Material(shader) { name = name }; material.SetColor("_BaseColor", color); renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
            var mesh = new Mesh { name = name }; mesh.MarkDynamic(); filter.sharedMesh = mesh; return mesh;
        }
        /// <summary>Toggles only the two lightweight line meshes.</summary>
        public void SetVisualizationEnabled(bool visible)
        { _visible = visible; if (_rayRenderer != null) _rayRenderer.enabled = visible; if (_markerRenderer != null) _markerRenderer.enabled = visible; }
        private Vector3 Origin => transform.position + (_owner.Role == AgentRole.GroundVehicle ? Vector3.up * 0.4f : Vector3.zero);
        private void FixedUpdate()
        {
            if (Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + 1 / scansPerSecond; ScanNow();
        }
        /// <summary>Casts every beam immediately on the physics thread; returns true only for relevant non-self colliders.</summary>
        public void ScanNow()
        {
            if (_directions == null) return;
            var origin = Origin; _hitCount = 0; _minimum = rangeMeters;
            for (var i = 0; i < beamCount; i++)
            {
                var fraction = fieldOfView >= 359.9f ? (float)i / beamCount : (float)i / (beamCount - 1);
                var direction = Quaternion.AngleAxis(-fieldOfView / 2 + fraction * fieldOfView, Vector3.up) * transform.forward;
                var count = Physics.RaycastNonAlloc(origin, direction, _hits, rangeMeters, collisionMask, QueryTriggerInteraction.Ignore);
                var nearest = rangeMeters; var hit = false;
                if (count == _hits.Length) { nearest = 0; hit = true; }
                for (var j = 0; j < count; j++) if (Relevant(_hits[j].collider) && _hits[j].distance < nearest) { nearest = _hits[j].distance; hit = true; }
                _directions[i] = direction; _distances[i] = hit ? nearest : -1; _ends[i] = origin + direction * nearest;
                if (hit) { _hitCount++; _minimum = Mathf.Min(_minimum, nearest); }
                _rayVertices[2 * i] = transform.InverseTransformPoint(origin); _rayVertices[2 * i + 1] = transform.InverseTransformPoint(_ends[i]);
                var point = transform.InverseTransformPoint(_ends[i]); var size = hit ? 0.055f : 0;
                for (var axis = 0; axis < 3; axis++)
                {
                    var delta = axis == 0 ? Vector3.right * size : axis == 1 ? Vector3.up * size : Vector3.forward * size;
                    _hitVertices[6 * i + 2 * axis] = point - delta; _hitVertices[6 * i + 2 * axis + 1] = point + delta;
                }
            }
            _sampledAt = Time.unscaledTime;
            _rays.vertices = _rayVertices; _rays.SetIndices(_rayIndices, MeshTopology.Lines, 0); _rays.RecalculateBounds();
            _markers.vertices = _hitVertices; _markers.SetIndices(_hitIndices, MeshTopology.Lines, 0); _markers.RecalculateBounds();
        }
        /// <summary>Fails closed for stale sensing, near forward hits, or colliders intersecting the next swept body.</summary>
        public bool BlocksTravel(Vector3 advance, float radius = 0.34f)
        {
            advance.y = 0; var distance = advance.magnitude;
            if (distance < 0.0001f) return false;
            if (_sampledAt < 0 || Time.unscaledTime - _sampledAt > 0.25f) return true;
            var direction = advance / distance;
            for (var i = 0; i < beamCount; i++)
                if (_distances[i] >= 0 && Vector3.Dot(direction, _directions[i]) > 0.5f && _distances[i] <= radius + distance + 0.15f) return true;
            var count = Physics.OverlapCapsuleNonAlloc(Origin, Origin + advance + direction * 0.12f, radius, _overlaps, collisionMask, QueryTriggerInteraction.Ignore);
            if (count == _overlaps.Length) return true;
            for (var i = 0; i < count; i++) if (Relevant(_overlaps[i])) return true;
            return false;
        }
        private bool Relevant(Collider collider) => collider != null && !collider.transform.IsChildOf(transform) &&
            (collider.GetComponentInParent<NavMeshObstacle>() != null || collider.GetComponentInParent<SwarmAgentController>() != null ||
             (collider.gameObject.isStatic && Mathf.Abs(collider.bounds.center.y - Origin.y) < collider.bounds.extents.y));
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private void OnDisable() { _sampledAt = -1; SetVisualizationEnabled(false); }
        private void OnDestroy()
        { if (_rays != null) Destroy(_rays); if (_markers != null) Destroy(_markers); if (_rayMaterial != null) Destroy(_rayMaterial); if (_hitMaterial != null) Destroy(_hitMaterial); }
    }
}
