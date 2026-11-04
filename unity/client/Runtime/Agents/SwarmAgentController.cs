using System;
using OmniTwin.Contracts;
using UnityEngine;
using UnityEngine.AI;
using OmniTwin.Simulation.Sensors;

namespace OmniTwin.Simulation.Agents
{
    /// <summary>Executes bounded ground NavMesh or aerial waypoint motion and reports measured simulation state.</summary>
    [DisallowMultipleComponent]
    public sealed class SwarmAgentController : MonoBehaviour
    {
        [SerializeField] private int agentId = 1;
        [SerializeField] private AgentRole role;
        [SerializeField] private float flightAltitude = 2.2f;
        [SerializeField] private float maximumSpeed = 4;
        [SerializeField] private float batteryDrainPerMeter = 0.0005f;
        [SerializeField] private LineRenderer trajectory;
        private NavMeshAgent _navigation;
        private NavMeshPath _navigationPath;
        private LidarSensor _lidar;
        private AgentTargetDirective _directive;
        private AgentStatus _status = AgentStatus.Holding;
        private Vector3 _previous, _velocity;
        private float _battery = 1, _pathRefresh;
        private readonly RaycastHit[] _hits = new RaycastHit[128];
        private readonly Vector3[] _corners = new Vector3[128];

        /// <summary>Gets this simulation entity's stable ID.</summary>
        public int AgentId => agentId;
        /// <summary>Gets its navigation class.</summary>
        public AgentRole Role => role;
        /// <summary>Gets its current operational state.</summary>
        public AgentStatus Status => _status;
        /// <summary>Gets the most recently sampled world velocity for rig tilt and callouts.</summary>
        public Vector3 MeasuredVelocity => _velocity;
        /// <summary>Gets the current policy-provided visualization mode tag, if moving.</summary>
        public string DirectiveStatusTag => _directive?.StatusTag;
        /// <summary>Gets the independently sampled local sensor.</summary>
        public LidarSensor Lidar => _lidar;
        /// <summary>Gets the current planar transform position without allocating telemetry.</summary>
        public Vector2D PlanarPosition => new Vector2D(transform.position.x, transform.position.z);

        /// <summary>Configures an authored entity before scene serialization or before enabling it.</summary>
        public void Configure(int id, AgentRole navigationRole, float altitude, LineRenderer line)
        { agentId = id; role = navigationRole; flightAltitude = altitude; trajectory = line; }

        private void Awake()
        {
            if (agentId < 0 || !Enum.IsDefined(typeof(AgentRole), role) || maximumSpeed <= 0 || maximumSpeed > 4 ||
                flightAltitude < 1 || float.IsNaN(flightAltitude) || float.IsInfinity(flightAltitude))
                throw new InvalidOperationException("Invalid authored swarm agent configuration.");
            EnsureRuntimeState();
            _previous = transform.position;
            if (role == AgentRole.GroundVehicle && (_navigation == null || !_navigation.isOnNavMesh))
                throw new InvalidOperationException("Ground agent must start on the baked navigation surface.");
            Hold();
        }

        private void OnEnable() => EnsureRuntimeState();

        private void EnsureRuntimeState()
        {
            if (_navigation == null) _navigation = GetComponent<NavMeshAgent>();
            if (_navigation != null && role == AgentRole.GroundVehicle)
            {
                // Match the avoidance disc to the visible chassis footprint (about 0.98 m long).
                _navigation.radius = Mathf.Max(_navigation.radius, 0.62f);
                _navigation.obstacleAvoidanceType = ObstacleAvoidanceType.HighQualityObstacleAvoidance;
            }
            if (_navigation != null && _navigationPath == null) _navigationPath = new NavMeshPath();
            if (_lidar == null) _lidar = GetComponent<LidarSensor>();
        }

        /// <summary>Applies one already validated directive. Zero speed immediately clears navigation.</summary>
        public void ApplyDirective(AgentTargetDirective directive)
        {
            if (directive == null || directive.AgentId != agentId || directive.DesiredSpeed > maximumSpeed || directive.DesiredSpeed < 0)
                throw new ArgumentException("Invalid agent directive.", nameof(directive));
            if (directive.DesiredSpeed == 0 || _battery <= 0.1f) { Hold(); return; }
            _directive = directive; _status = AgentStatus.Navigating;
            if (role == AgentRole.GroundVehicle)
            {
                if (!_navigation.isOnNavMesh) { Hold(); return; }
                var target = new Vector3(directive.TargetPosition.X, 0, directive.TargetPosition.Z);
                if (!NavMesh.SamplePosition(target, out var hit, 0.4f, _navigation.areaMask)) { Hold(); return; }
                _navigation.speed = Mathf.Min(maximumSpeed, directive.DesiredSpeed); _navigation.isStopped = false;
                if (!_navigation.SetDestination(hit.position)) Hold();
            }
        }

        /// <summary>Stops the entity synchronously on the Unity main thread, independently of network cancellation.</summary>
        public void Hold()
        {
            _directive = null; _velocity = Vector3.zero;
            _status = _battery <= 0.1f ? AgentStatus.LowBattery : AgentStatus.Holding;
            if (_navigation != null && _navigation.enabled && _navigation.isOnNavMesh)
            { _navigation.isStopped = true; _navigation.ResetPath(); _navigation.velocity = Vector3.zero; }
            if (trajectory != null) trajectory.positionCount = 0;
        }

        /// <summary>Samples actual transform/navigation state; battery represents the configured simulation energy model.</summary>
        public AgentTelemetry ReadTelemetry() => new AgentTelemetry(agentId, role, new Vector2D(transform.position.x, transform.position.z),
            new Vector2D(_velocity.x, _velocity.z), _battery, _status, _lidar != null ? _lidar.ReadTelemetry() : null);

        private void FixedUpdate()
        {
            var position = transform.position;
            var moved = Vector3.Distance(position, _previous);
            _velocity = (position - _previous) / Mathf.Max(Time.fixedDeltaTime, 0.001f);
            _previous = position;
            _battery = Mathf.Clamp01(_battery - moved * batteryDrainPerMeter - Time.fixedDeltaTime * 0.00001f);
            if (_battery <= 0.1f) { Hold(); return; }
            if (_directive == null) return;
            var goal = new Vector3(_directive.TargetPosition.X, role == AgentRole.AerialDrone ? flightAltitude : position.y, _directive.TargetPosition.Z);
            if (role == AgentRole.GroundVehicle)
            {
                if (!_navigation.isOnNavMesh || (!_navigation.pathPending && _navigation.hasPath && _navigation.pathStatus != NavMeshPathStatus.PathComplete)) { Hold(); return; }
                var advance = _navigation.desiredVelocity * Time.fixedDeltaTime;
                if (Blocked(position, advance)) { Hold(); return; }
                if (!_navigation.pathPending && _navigation.remainingDistance <= _navigation.stoppingDistance)
                    _status = AgentStatus.Idle;
            }
            else
            {
                var next = Vector3.MoveTowards(position, goal, Mathf.Min(maximumSpeed, _directive.DesiredSpeed) * Time.fixedDeltaTime);
                if (Blocked(position, next - position)) { Hold(); return; }
                transform.position = next;
                if (Vector3.Distance(next, goal) <= 0.05f) _status = AgentStatus.Idle;
                var forward = goal - position; forward.y = 0;
                if (forward.sqrMagnitude > 0.001f) transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(forward), 180 * Time.fixedDeltaTime);
            }
        }

        private bool Blocked(Vector3 position, Vector3 advance)
        {
            if (_lidar == null || !_lidar.isActiveAndEnabled || _lidar.BlocksTravel(advance)) return true;
            advance.y = 0; var distance = advance.magnitude;
            if (distance < 0.0001f) return false;
            var count = Physics.SphereCastNonAlloc(new Vector3(position.x, 0.4f, position.z), 0.34f, advance / distance,
                _hits, distance + 0.12f, ~0, QueryTriggerInteraction.Ignore);
            if (count == _hits.Length) return true;
            for (var i = 0; i < count; i++)
                if (_hits[i].collider != null && _hits[i].collider.GetComponentInParent<NavMeshObstacle>() != null) return true;
            return false;
        }

        private void LateUpdate()
        {
            if (trajectory == null || _directive == null || Time.unscaledTime < _pathRefresh) return;
            _pathRefresh = Time.unscaledTime + 0.1f;
            if (role == AgentRole.GroundVehicle)
            {
                EnsureRuntimeState();
                if (_navigation == null || !_navigation.hasPath) { trajectory.positionCount = 0; return; }
                if (!NavMesh.CalculatePath(transform.position, _navigation.destination, _navigation.areaMask, _navigationPath))
                { trajectory.positionCount = 0; return; }
                var count = _navigationPath.GetCornersNonAlloc(_corners); trajectory.positionCount = count;
                for (var i = 0; i < count; i++) trajectory.SetPosition(i, _corners[i] + Vector3.up * 0.08f);
            }
            else
            {
                trajectory.positionCount = 2; trajectory.SetPosition(0, transform.position + Vector3.up * 0.08f);
                trajectory.SetPosition(1, new Vector3(_directive.TargetPosition.X, flightAltitude, _directive.TargetPosition.Z));
            }
        }
        private void OnDisable() => Hold();
    }
}
