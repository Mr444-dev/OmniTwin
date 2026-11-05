using System;
using System.Collections.Generic;
using System.Globalization;
using OmniTwin.Contracts;
using OmniTwin.Policies;
using OmniTwin.Simulation.Agents;
using OmniTwin.Simulation.Network;
using OmniTwin.Simulation.Runtime;
using OmniTwin.Transport;
using UnityEngine;
using UnityEngine.Rendering;

namespace OmniTwin.Simulation.World
{
    /// <summary>🗺️ Adds a graphite facility surround, calibration marks, and mission geometry without changing scene assets.</summary>
    [DisallowMultipleComponent]
    public sealed class TacticalScenePresentation : MonoBehaviour
    {
        private const float GroundY = 0.055f;
        private const int RingPointCount = 65;
        private static readonly Color Cyan = new Color(0.13f, 0.92f, 0.82f, 0.78f);
        private static readonly Color Amber = new Color(1f, 0.55f, 0.16f, 0.88f);
        private static readonly Color SearchGreen = new Color(0.2f, 0.78f, 0.65f, 0.19f);
        private static readonly Color PathBlue = new Color(0.2f, 0.77f, 1f, 0.65f);
        private readonly List<Transform> _billboards = new List<Transform>(12);
        private readonly List<LineRenderer> _searchLines = new List<LineRenderer>(48);
        private readonly List<Interval> _blockedIntervals = new List<Interval>(16);
        private readonly List<PathVisual> _agentPaths = new List<PathVisual>(8);
        private readonly Vector3[] _circle = new Vector3[RingPointCount];
        private readonly Vector3[] _patrolPoints = new Vector3[5];
        private TwinWebSocketClient _stream;
        private PolicyExecutor _executor;
        private DynamicObstacleSpawner _obstacleSpawner;
        private Camera _camera;
        private Material _facilityMaterial;
        private Material _lineMaterial;
        private Material _gridMaterial;
        private Material _accentMaterial;
        private Material _hazardMaterial;
        private Material _pathMaterial;
        private Material _searchMaterial;
        private Mesh _gridMesh;
        private LineRenderer _patrolRoute;
        private LineRenderer _quarantineRing;
        private LineRenderer _operatorHazardRing;
        private int _operatorHazardTrackId = -1;
        private float _nextRefresh;
        private int _searchLinesUsed;

        /// <summary>🧩 Installs a runtime-only presentation root in every loaded tactical scene.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (UnityEngine.Object.FindAnyObjectByType<TacticalScenePresentation>() != null) return;
            new GameObject("OmniTwin Tactical Scene Presentation").AddComponent<TacticalScenePresentation>();
        }

        private void Awake()
        {
            _stream = UnityEngine.Object.FindAnyObjectByType<TwinWebSocketClient>();
            _executor = UnityEngine.Object.FindAnyObjectByType<PolicyExecutor>();
            _obstacleSpawner = UnityEngine.Object.FindAnyObjectByType<DynamicObstacleSpawner>();
            _camera = Camera.main;
            CreateMaterials();
            CreateFacilitySurround();
            CreateCalibrationAnnotations();
            CreateMissionGeometry();
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 0.12f;
            if (_camera == null) _camera = Camera.main;
            if (_camera != null)
            {
                _camera.clearFlags = CameraClearFlags.SolidColor;
                _camera.backgroundColor = new Color(0.06f, 0.078f, 0.095f, 1f);
            }
            FaceAnnotations();

            WorldStreamClientView view = default;
            var hasWorld = _stream != null && _stream.TryGetCurrentView(out view);
            var obstacles = hasWorld ? view.State.Message.World.Obstacles : null;
            var policyName = _executor == null ? nameof(EmergencyHoldPolicy) : _executor.ActivePolicyName;
            var mission = _executor == null ? null : _executor.CurrentMission;
            var kind = mission != null ? mission.Kind : KindFromPolicy(policyName);
            var active = hasWorld && (_executor != null && (_executor.IsLocalMissionSelected
                ? _executor.IsLocalMissionMoving
                : policyName != nameof(EmergencyHoldPolicy)));

            _patrolRoute.enabled = active && kind == SwarmMissionKind.PatrolPerimeter;
            _quarantineRing.enabled = active && kind == SwarmMissionKind.HazardQuarantine;
            UpdateAgentPaths(active);
            UpdateQuarantine(obstacles, active && kind == SwarmMissionKind.HazardQuarantine,
                mission != null ? mission.SafetyRadiusMeters : 3f);
            UpdateSearchLanes(obstacles, active && kind == SwarmMissionKind.GridSearchAndRescue);
            UpdateOperatorHazardRing(obstacles);
        }

        /// <summary>Applies a local hazard visual to an existing current track without claiming a model/API classification.</summary>
        public bool SetOperatorHazardOverride(int trackId, bool isHazard)
        {
            if (_obstacleSpawner == null) _obstacleSpawner = UnityEngine.Object.FindAnyObjectByType<DynamicObstacleSpawner>();
            if (_obstacleSpawner == null || !_obstacleSpawner.SetOperatorHazardOverride(trackId, isHazard)) return false;
            _operatorHazardTrackId = isHazard ? trackId : -1;
            if (!isHazard && _operatorHazardRing != null) _operatorHazardRing.enabled = false;
            return true;
        }

        private void UpdateOperatorHazardRing(IReadOnlyList<TrackedObstacleState> tracks)
        {
            if (_operatorHazardTrackId < 0 || tracks == null || _operatorHazardRing == null)
            { if (_operatorHazardRing != null) _operatorHazardRing.enabled = false; return; }
            foreach (var track in tracks)
            {
                if (track.Obstacle.Id != _operatorHazardTrackId) continue;
                var position = track.Obstacle.WorldPosition;
                SetCircle(_operatorHazardRing, new Vector3(position.X, GroundY + 0.04f, position.Z),
                    Mathf.Max(1.2f, track.Obstacle.RadiusMeters + 0.9f));
                _operatorHazardRing.enabled = true;
                return;
            }
            _operatorHazardRing.enabled = false;
        }

        private void CreateMaterials()
        {
            var unlit = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            if (unlit == null) throw new InvalidOperationException("OmniTwin tactical presentation requires a URP Unlit shader.");
            _facilityMaterial = new Material(unlit) { name = "OmniTwin Graphite Facility" };
            SetMaterialColor(_facilityMaterial, new Color(0.06f, 0.078f, 0.095f, 1f));
            _lineMaterial = new Material(unlit) { name = "OmniTwin Tactical Geometry" };
            SetMaterialColor(_lineMaterial, Color.white);
            _gridMaterial = CreateTintedMaterial(unlit, "OmniTwin Facility Grid", new Color(0.12f, 0.15f, 0.17f, 1f), false);
            _accentMaterial = CreateTintedMaterial(unlit, "OmniTwin Patrol Geometry", Cyan, true);
            _hazardMaterial = CreateTintedMaterial(unlit, "OmniTwin Hazard Geometry", Amber, true);
            _pathMaterial = CreateTintedMaterial(unlit, "OmniTwin Agent Target Geometry", PathBlue, true);
            _searchMaterial = CreateTintedMaterial(unlit, "OmniTwin Search Geometry", SearchGreen, true);
        }

        private static Material CreateTintedMaterial(Shader shader, string name, Color color, bool transparent)
        {
            var material = new Material(shader) { name = name };
            SetMaterialColor(material, color);
            if (transparent && material.HasProperty("_Surface"))
            {
                material.SetFloat("_Surface", 1f); material.SetFloat("_Blend", 0f); material.SetFloat("_ZWrite", 0f);
                material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha); material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); material.renderQueue = (int)RenderQueue.Transparent;
            }
            return material;
        }

        private static void SetMaterialColor(Material material, Color color)
        {
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            else if (material.HasProperty("_Color")) material.SetColor("_Color", color);
        }

        private void CreateFacilitySurround()
        {
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Graphite Facility Floor";
            floor.transform.SetParent(transform, false);
            floor.transform.position = new Vector3(0, -0.31f, 0);
            floor.transform.localScale = new Vector3(12f, 1f, 8f);
            floor.GetComponent<Renderer>().sharedMaterial = _facilityMaterial;
            var collider = floor.GetComponent<Collider>();
            if (collider != null) Destroy(collider);

            // Explicit apron slabs keep the facility visible outside the calibrated desk even when
            // the authored desk renderer or a camera clear mode hides the large under-floor plane.
            CreateApronSlab("Facility apron north", new Vector3(0f, -0.005f, 20.35f), new Vector3(90f, 0.03f, 27.8f));
            CreateApronSlab("Facility apron south", new Vector3(0f, -0.005f, -20.35f), new Vector3(90f, 0.03f, 27.8f));
            CreateApronSlab("Facility apron east", new Vector3(27.6f, -0.005f, 0f), new Vector3(35.2f, 0.03f, 12.5f));
            CreateApronSlab("Facility apron west", new Vector3(-27.6f, -0.005f, 0f), new Vector3(35.2f, 0.03f, 12.5f));

            var gridVertices = new List<Vector3>(192);
            var gridIndices = new List<int>(192);
            for (var z = -30; z <= 30; z += 2)
                if (Mathf.Abs(z) >= 8)
                    AppendGridLine(gridVertices, gridIndices, new Vector3(-44, 0.026f, z), new Vector3(44, 0.026f, z));
            for (var x = -44; x <= 44; x += 2)
                if (Mathf.Abs(x) >= 12)
                    AppendGridLine(gridVertices, gridIndices, new Vector3(x, 0.026f, -7.8f), new Vector3(x, 0.026f, 7.8f));
            var grid = new GameObject("Facility apron grid mesh"); grid.transform.SetParent(transform, false);
            _gridMesh = new Mesh { name = "OmniTwin static facility grid" };
            _gridMesh.SetVertices(gridVertices); _gridMesh.SetIndices(gridIndices, MeshTopology.Lines, 0, true);
            _gridMesh.UploadMeshData(true);
            grid.AddComponent<MeshFilter>().sharedMesh = _gridMesh;
            var gridRenderer = grid.AddComponent<MeshRenderer>(); gridRenderer.sharedMaterial = _gridMaterial;
            gridRenderer.shadowCastingMode = ShadowCastingMode.Off; gridRenderer.receiveShadows = false;
        }

        private void CreateApronSlab(string objectName, Vector3 position, Vector3 size)
        {
            var slab = GameObject.CreatePrimitive(PrimitiveType.Cube);
            slab.name = objectName;
            slab.transform.SetParent(transform, false);
            slab.transform.position = position;
            slab.transform.localScale = size;
            slab.GetComponent<Renderer>().sharedMaterial = _facilityMaterial;
            var collider = slab.GetComponent<Collider>();
            if (collider != null) Destroy(collider);
        }

        private static void AppendGridLine(List<Vector3> vertices, List<int> indices, Vector3 start, Vector3 end)
        {
            var first = vertices.Count;
            vertices.Add(start); vertices.Add(end);
            indices.Add(first); indices.Add(first + 1);
        }

        private void CreateCalibrationAnnotations()
        {
            var corners = new[]
            {
                new Corner("CAM NW", -10f, 6.25f, -1, 1),
                new Corner("CAM NE", 10f, 6.25f, 1, 1),
                new Corner("CAM SE", 10f, -6.25f, 1, -1),
                new Corner("CAM SW", -10f, -6.25f, -1, -1)
            };
            foreach (var corner in corners)
            {
                var p = new Vector3(corner.X, 0.045f, corner.Z);
                CreateLine(corner.Name + " calibration X", p + Vector3.left * 0.22f, p + Vector3.right * 0.22f, Color.white, 0.035f, _accentMaterial);
                CreateLine(corner.Name + " calibration Z", p + Vector3.back * 0.22f, p + Vector3.forward * 0.22f, Color.white, 0.035f, _accentMaterial);
                var label = CreateLabel(corner.Name, new Vector3(corner.X - corner.OutX * 0.65f, 0.13f, corner.Z - corner.OutZ * 0.5f),
                    new Color(0.43f, 0.93f, 0.88f, 1f), 0.040f, _accentMaterial);
                label.anchor = corner.OutX < 0 ? TextAnchor.LowerLeft : TextAnchor.LowerRight;
                label.alignment = corner.OutX < 0 ? TextAlignment.Left : TextAlignment.Right;
            }

            CreateLine("Workspace dimension X", new Vector3(-10, 0.04f, 7.05f), new Vector3(10, 0.04f, 7.05f), Color.white, 0.025f, _accentMaterial);
            CreateLine("Workspace dimension Z", new Vector3(11.05f, 0.04f, -6.25f), new Vector3(11.05f, 0.04f, 6.25f), Color.white, 0.025f, _accentMaterial);
            CreateLabel("20.0m × 12.5m CALIBRATED WORKSPACE", new Vector3(0, 0.14f, 7.35f),
                new Color(0.61f, 0.81f, 0.84f, 1f), 0.036f, _accentMaterial);
        }

        private void CreateMissionGeometry()
        {
            _patrolPoints[0] = new Vector3(-9.25f, GroundY, -5.5f);
            _patrolPoints[1] = new Vector3(9.25f, GroundY, -5.5f);
            _patrolPoints[2] = new Vector3(9.25f, GroundY, 5.5f);
            _patrolPoints[3] = new Vector3(-9.25f, GroundY, 5.5f);
            _patrolPoints[4] = _patrolPoints[0];
            _patrolRoute = CreatePolyline("Perimeter patrol route", _accentMaterial, 0.045f, 5);
            _patrolRoute.SetPositions(_patrolPoints);

            var waypointNames = new[] { "WP-1", "WP-2", "WP-3", "WP-4" };
            for (var i = 0; i < 4; i++)
                CreateLabel(waypointNames[i], _patrolPoints[i] + new Vector3(i < 2 ? -0.45f : 0.45f, 0.18f, i == 0 || i == 3 ? -0.2f : 0.2f),
                    new Color(0.4f, 0.98f, 0.84f, 1f), 0.030f, _accentMaterial);

            _quarantineRing = CreatePolyline("Hazard quarantine safety ring", _hazardMaterial, 0.055f, RingPointCount);
            _quarantineRing.loop = false;
            _operatorHazardRing = CreatePolyline("Local operator hazard override ring", _hazardMaterial, 0.075f, RingPointCount);
            _operatorHazardRing.loop = false;
            if (_executor != null)
                foreach (var agent in _executor.Agents)
                {
                    var link = CreatePolyline("Agent " + agent.AgentId.ToString(CultureInfo.InvariantCulture) + " target line", _pathMaterial, 0.026f, 2);
                    var marker = CreatePolyline("Agent " + agent.AgentId.ToString(CultureInfo.InvariantCulture) + " target marker", _accentMaterial, 0.04f, RingPointCount);
                    _agentPaths.Add(new PathVisual(agent, link, marker));
                }
        }

        private void UpdateAgentPaths(bool active)
        {
            foreach (var path in _agentPaths)
            {
                var directive = path.Agent.CurrentDirective;
                var visible = active && directive != null && directive.DesiredSpeed > 0;
                path.Line.enabled = visible;
                path.Marker.enabled = visible;
                if (!visible) continue;
                var position = path.Agent.transform.position;
                var target = directive.TargetPosition;
                var start = new Vector3(position.x, GroundY + 0.015f, position.z);
                var end = new Vector3(target.X, GroundY + 0.015f, target.Z);
                path.Line.SetPosition(0, start); path.Line.SetPosition(1, end);
                SetCircle(path.Marker, end, 0.23f);
            }
        }

        private void UpdateQuarantine(IReadOnlyList<TrackedObstacleState> tracks, bool visible, float requestedRadius)
        {
            if (!visible || tracks == null || tracks.Count == 0) { _quarantineRing.enabled = false; return; }
            SegmentedObstacle selected = null;
            foreach (var track in tracks)
            {
                var item = track.Obstacle;
                if (selected == null || (item.IsHazardZone && !selected.IsHazardZone) ||
                    (item.IsHazardZone == selected.IsHazardZone && (item.RadiusMeters > selected.RadiusMeters ||
                    (Mathf.Approximately(item.RadiusMeters, selected.RadiusMeters) && item.Id < selected.Id)))) selected = item;
            }
            if (selected == null) { _quarantineRing.enabled = false; return; }
            var ringRadius = Mathf.Max(requestedRadius, selected.RadiusMeters + 0.8f);
            SetCircle(_quarantineRing, new Vector3(selected.WorldPosition.X, GroundY + 0.025f, selected.WorldPosition.Z), ringRadius);
            _quarantineRing.enabled = true;
        }

        private void UpdateSearchLanes(IReadOnlyList<TrackedObstacleState> tracks, bool visible)
        {
            if (!visible)
            {
                for (var i = 0; i < _searchLinesUsed; i++) _searchLines[i].enabled = false;
                _searchLinesUsed = 0;
                return;
            }

            _searchLinesUsed = 0;
            for (var lane = 0; lane < 11; lane++)
            {
                var z = -5f + lane;
                _blockedIntervals.Clear();
                if (tracks != null)
                    foreach (var track in tracks)
                    {
                        var obstacle = track.Obstacle;
                        var dz = z - obstacle.WorldPosition.Z;
                        var radius = obstacle.RadiusMeters + 0.42f;
                        if (Mathf.Abs(dz) >= radius) continue;
                        var half = Mathf.Sqrt(Mathf.Max(0, radius * radius - dz * dz));
                        var left = Mathf.Max(-9.35f, obstacle.WorldPosition.X - half);
                        var right = Mathf.Min(9.35f, obstacle.WorldPosition.X + half);
                        if (right > left) _blockedIntervals.Add(new Interval(left, right));
                    }
                _blockedIntervals.Sort((a, b) => a.Start.CompareTo(b.Start));
                var cursor = -9.35f;
                foreach (var blocked in _blockedIntervals)
                {
                    if (blocked.Start - cursor > 0.4f)
                        SetSearchSegment(_searchLinesUsed++, new Vector3(cursor, GroundY + 0.005f, z), new Vector3(blocked.Start, GroundY + 0.005f, z));
                    cursor = Mathf.Max(cursor, blocked.End);
                    if (cursor >= 9.35f) break;
                }
                if (9.35f - cursor > 0.4f)
                    SetSearchSegment(_searchLinesUsed++, new Vector3(cursor, GroundY + 0.005f, z), new Vector3(9.35f, GroundY + 0.005f, z));
            }
            for (var i = _searchLinesUsed; i < _searchLines.Count; i++) _searchLines[i].enabled = false;
        }

        private void SetSearchSegment(int index, Vector3 start, Vector3 end)
        {
            while (_searchLines.Count <= index)
                _searchLines.Add(CreatePolyline("Grid search free lane " + _searchLines.Count.ToString(CultureInfo.InvariantCulture), _searchMaterial, 0.028f, 2));
            var line = _searchLines[index]; line.SetPosition(0, start); line.SetPosition(1, end); line.enabled = true;
        }

        private void FaceAnnotations()
        {
            if (_camera == null) return;
            var cameraTransform = _camera.transform;
            foreach (var label in _billboards)
                if (label != null) label.rotation = Quaternion.LookRotation(label.position - cameraTransform.position, cameraTransform.up);
        }

        private TextMesh CreateLabel(string text, Vector3 position, Color color, float characterSize, Material material = null)
        {
            var gameObject = new GameObject("Tactical annotation " + text);
            gameObject.transform.SetParent(transform, false); gameObject.transform.position = position;
            var label = gameObject.AddComponent<TextMesh>(); label.text = text; label.fontSize = 72;
            label.characterSize = characterSize; label.anchor = TextAnchor.MiddleCenter; label.alignment = TextAlignment.Center;
            label.color = color; label.richText = false;
            var renderer = label.GetComponent<MeshRenderer>(); renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false; renderer.sharedMaterial = material != null ? material : _lineMaterial;
            _billboards.Add(gameObject.transform);
            return label;
        }

        private LineRenderer CreateLine(string name, Vector3 start, Vector3 end, Color color, float width, Material material = null)
        {
            var line = CreatePolyline(name, material, width, 2);
            line.startColor = color; line.endColor = color;
            line.SetPosition(0, start); line.SetPosition(1, end);
            return line;
        }

        private LineRenderer CreatePolyline(string name, Material material, float width, int count)
        {
            var gameObject = new GameObject(name); gameObject.transform.SetParent(transform, false);
            var line = gameObject.AddComponent<LineRenderer>(); line.useWorldSpace = true; line.loop = false; line.positionCount = count;
            line.sharedMaterial = material != null ? material : _lineMaterial; line.startWidth = width; line.endWidth = width;
            line.startColor = Color.white; line.endColor = Color.white;
            line.numCapVertices = 2; line.numCornerVertices = 1; line.shadowCastingMode = ShadowCastingMode.Off; line.receiveShadows = false;
            return line;
        }

        private void SetCircle(LineRenderer line, Vector3 center, float radius)
        {
            for (var i = 0; i < RingPointCount; i++)
            {
                var angle = (float)(i * (Math.PI * 2 / (RingPointCount - 1)));
                _circle[i] = center + new Vector3(Mathf.Cos(angle) * radius, 0, Mathf.Sin(angle) * radius);
            }
            line.SetPositions(_circle);
        }

        private static SwarmMissionKind KindFromPolicy(string policyName)
        {
            if (policyName == nameof(PerimeterPatrolPolicy)) return SwarmMissionKind.PatrolPerimeter;
            if (policyName == nameof(HazardQuarantinePolicy)) return SwarmMissionKind.HazardQuarantine;
            if (policyName == nameof(GridSearchAndRescuePolicy)) return SwarmMissionKind.GridSearchAndRescue;
            return SwarmMissionKind.HoldPosition;
        }

        private void OnDestroy()
        {
            if (_facilityMaterial != null) Destroy(_facilityMaterial);
            if (_lineMaterial != null) Destroy(_lineMaterial);
            if (_gridMaterial != null) Destroy(_gridMaterial);
            if (_accentMaterial != null) Destroy(_accentMaterial);
            if (_hazardMaterial != null) Destroy(_hazardMaterial);
            if (_pathMaterial != null) Destroy(_pathMaterial);
            if (_searchMaterial != null) Destroy(_searchMaterial);
            if (_gridMesh != null) Destroy(_gridMesh);
        }

        private readonly struct Corner
        {
            internal Corner(string name, float x, float z, int outX, int outZ)
            { Name = name; X = x; Z = z; OutX = outX; OutZ = outZ; }
            internal readonly string Name;
            internal readonly float X, Z;
            internal readonly int OutX, OutZ;
        }

        private readonly struct Interval
        {
            internal Interval(float start, float end) { Start = start; End = end; }
            internal readonly float Start, End;
        }

        private sealed class PathVisual
        {
            internal PathVisual(SwarmAgentController agent, LineRenderer line, LineRenderer marker)
            { Agent = agent; Line = line; Marker = marker; }
            internal readonly SwarmAgentController Agent;
            internal readonly LineRenderer Line, Marker;
        }
    }
}
