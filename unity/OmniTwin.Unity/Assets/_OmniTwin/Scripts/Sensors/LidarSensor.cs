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
        // Show sparse empty-space coverage while drawing every actual impact.
        private const int RenderedBeamStride = 8;
        private const float ClearBeamVisualRangeMeters = 1.1f;
        [SerializeField, Range(8, 128)] private int beamCount = 48;
        [SerializeField, Range(30, 360)] private float fieldOfView = 360;
        [SerializeField, Range(0.5f, 30)] private float rangeMeters = 6;
        [SerializeField, Range(5, 50)] private float scansPerSecond = 20;
        [SerializeField] private LayerMask collisionMask = ~0;
        private readonly RaycastHit[] _hits = new RaycastHit[128];
        private readonly Collider[] _overlaps = new Collider[128];
        private Vector3[] _directions, _hitVertices;
        private float[] _distances, _blockingDistances;
        private bool[] _renderedBeams;
        private int[] _hitIndices;
        private int _hitCount;
        private float _sampledAt = -1, _nextScan, _minimum;
        private bool _visible;
        private SwarmAgentController _owner;
        private Mesh _markers;
        private LineRenderer[] _rayLines;
        private MeshRenderer _markerRenderer;
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
            _directions = new Vector3[beamCount]; _distances = new float[beamCount]; _blockingDistances = new float[beamCount]; _renderedBeams = new bool[beamCount];
            _hitVertices = new Vector3[beamCount * 6]; _hitIndices = new int[_hitVertices.Length];
            for (var i = 0; i < _hitIndices.Length; i++) _hitIndices[i] = i;
            CreateRayLines();
            _markers = Visual("LiDAR hit markers", new Color(1, 0.48f, 0.12f, 1f), false, out _markerRenderer, out _hitMaterial);
            SetVisualizationEnabled(false);
        }

        private void CreateRayLines()
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) throw new InvalidOperationException("URP Unlit is required for LiDAR.");
            _rayMaterial = new Material(shader) { name = "LiDAR beam material" };
            _rayMaterial.SetColor("_BaseColor", Color.white);
            _rayMaterial.SetFloat("_Surface", 1); _rayMaterial.SetFloat("_Blend", 0); _rayMaterial.SetFloat("_ZWrite", 0);
            _rayMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha); _rayMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _rayMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); _rayMaterial.renderQueue = (int)RenderQueue.Transparent;
            _rayLines = new LineRenderer[beamCount];
            for (var index = 0; index < beamCount; index++)
            {
                var child = new GameObject("LiDAR sampled beam " + index);
                child.transform.SetParent(transform, false);
                var line = child.AddComponent<LineRenderer>();
                line.useWorldSpace = false; line.loop = false; line.positionCount = 2; line.sharedMaterial = _rayMaterial;
                line.alignment = LineAlignment.View; line.numCapVertices = 2; line.numCornerVertices = 0;
                line.shadowCastingMode = ShadowCastingMode.Off; line.receiveShadows = false;
                line.enabled = false; _rayLines[index] = line;
            }
        }
        private Mesh Visual(string name, Color color, bool transparent, out MeshRenderer renderer, out Material material)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) throw new InvalidOperationException("URP Unlit is required for LiDAR.");
            var child = new GameObject(name); child.transform.SetParent(transform, false);
            var filter = child.AddComponent<MeshFilter>(); renderer = child.AddComponent<MeshRenderer>();
            material = new Material(shader) { name = name }; material.SetColor("_BaseColor", color); renderer.sharedMaterial = material;
            if (transparent)
            {
                material.SetFloat("_Surface", 1); material.SetFloat("_Blend", 0); material.SetFloat("_ZWrite", 0);
                material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha); material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); material.renderQueue = (int)RenderQueue.Transparent;
            }
            renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
            var mesh = new Mesh { name = name }; mesh.MarkDynamic(); filter.sharedMesh = mesh; return mesh;
        }
        /// <summary>Toggles only the sampled line renderers and impact marker mesh; physics scanning continues.</summary>
        public void SetVisualizationEnabled(bool visible)
        {
            _visible = visible;
            if (_rayLines != null)
                for (var i = 0; i < _rayLines.Length; i++)
                    if (_rayLines[i] != null) _rayLines[i].enabled = visible && _renderedBeams[i];
            if (_markerRenderer != null) _markerRenderer.enabled = visible;
        }
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
                var nearest = rangeMeters; var nearestBlocker = rangeMeters; var hit = false; var blockingHit = false;
                if (count == _hits.Length) { nearest = nearestBlocker = 0; hit = blockingHit = true; }
                for (var j = 0; j < count; j++)
                {
                    var candidate = _hits[j];
                    if (!Relevant(candidate.collider)) continue;
                    if (candidate.distance < nearest) { nearest = candidate.distance; hit = true; }
                    if (BlocksTravelCollider(candidate.collider) && candidate.distance < nearestBlocker)
                    { nearestBlocker = candidate.distance; blockingHit = true; }
                }
                _directions[i] = direction; _distances[i] = hit ? nearest : -1;
                _blockingDistances[i] = blockingHit ? nearestBlocker : -1;
                if (hit) { _hitCount++; _minimum = Mathf.Min(_minimum, nearest); }
                var renderBeam = hit || i % RenderedBeamStride == 0;
                _renderedBeams[i] = renderBeam;
                var line = _rayLines[i];
                if (renderBeam)
                {
                    var visualDistance = hit ? nearest : Mathf.Min(rangeMeters, ClearBeamVisualRangeMeters);
                    line.SetPosition(0, transform.InverseTransformPoint(origin));
                    line.SetPosition(1, transform.InverseTransformPoint(origin + direction * visualDistance));
                    line.startWidth = line.endWidth = hit ? 0.022f : 0.008f;
                    var color = hit ? new Color(0.12f, 0.9f, 1f, 0.9f) : new Color(0.12f, 0.75f, 0.88f, 0.22f);
                    line.startColor = color; line.endColor = color; line.enabled = _visible;
                }
                else if (line.enabled) line.enabled = false;
                var point = transform.InverseTransformPoint(origin + direction * nearest); var size = hit ? 0.055f : 0;
                for (var axis = 0; axis < 3; axis++)
                {
                    var delta = axis == 0 ? Vector3.right * size : axis == 1 ? Vector3.up * size : Vector3.forward * size;
                    _hitVertices[6 * i + 2 * axis] = point - delta; _hitVertices[6 * i + 2 * axis + 1] = point + delta;
                }
            }
            _sampledAt = Time.unscaledTime;
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
                if (_blockingDistances[i] >= 0 && Vector3.Dot(direction, _directions[i]) > 0.5f && _blockingDistances[i] <= radius + distance + 0.15f) return true;
            var count = Physics.OverlapCapsuleNonAlloc(Origin, Origin + advance + direction * 0.12f, radius, _overlaps, collisionMask, QueryTriggerInteraction.Ignore);
            if (count == _overlaps.Length) return true;
            for (var i = 0; i < count; i++) if (BlocksTravelCollider(_overlaps[i])) return true;
            return false;
        }
        private bool Relevant(Collider collider) => collider != null && !collider.transform.IsChildOf(transform) &&
            (collider.GetComponentInParent<NavMeshObstacle>() != null || collider.GetComponentInParent<SwarmAgentController>() != null ||
             (collider.gameObject.isStatic && Mathf.Abs(collider.bounds.center.y - Origin.y) < collider.bounds.extents.y));
        private bool BlocksTravelCollider(Collider collider)
        {
            if (collider == null || collider.transform.IsChildOf(transform)) return false;
            if (collider.GetComponentInParent<NavMeshObstacle>() != null) return true;
            var otherAgent = collider.GetComponentInParent<SwarmAgentController>();
            if (otherAgent != null && _owner.Role == AgentRole.GroundVehicle && otherAgent.Role == AgentRole.GroundVehicle) return false;
            return collider.gameObject.isStatic && Mathf.Abs(collider.bounds.center.y - Origin.y) < collider.bounds.extents.y;
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private void OnDisable() { _sampledAt = -1; SetVisualizationEnabled(false); }
        private void OnDestroy()
        { if (_markers != null) Destroy(_markers); if (_rayMaterial != null) Destroy(_rayMaterial); if (_hitMaterial != null) Destroy(_hitMaterial); }
    }
}
