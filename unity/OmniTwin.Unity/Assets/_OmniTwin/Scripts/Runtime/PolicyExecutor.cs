using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OmniTwin.Contracts;
using OmniTwin.Policies;
using OmniTwin.Simulation.Agents;
using OmniTwin.Simulation.Network;
using OmniTwin.Transport;
using UnityEngine;

namespace OmniTwin.Simulation.Runtime
{
    /// <summary>Consumes validated data-only controls and immediately holds on source, session, or transport loss.</summary>
    [DisallowMultipleComponent]
    public sealed class PolicyExecutor : MonoBehaviour
    {
        [SerializeField] private TwinWebSocketClient stream;
        [SerializeField] private SwarmAgentController[] agents = new SwarmAgentController[0];
        [SerializeField] private string backendOrigin = "http://127.0.0.1:5108";
        private SwarmControlClient _client;
        private CancellationTokenSource _stop;
        private Task _worker;
        private Guid _session;
        private long _sequence;
        private float _publishAt;
        private SwarmControlSnapshot _applied;
        private SwarmMissionCommand _currentMission;
        private IReadOnlyList<SwarmAgentController> _agentView;
        private bool _holding = true;
        private string _localFailure;
        private ISwarmPolicy _localPolicy;
        private bool _localMissionUsesBrain;
        private long _missionTelemetrySequence;
        private string _localExecutionStatus = "Waiting for a current observation";
        private readonly List<SegmentedObstacle> _obstacles = new List<SegmentedObstacle>();
        private readonly HashSet<int> _geometryBlockedAgents = new HashSet<int>();
        private readonly HashSet<int> _spawnRecoveryAttempted = new HashSet<int>();
        private readonly Dictionary<int, bool> _operatorHazards = new Dictionary<int, bool>();
        private Guid _operatorHazardSessionId;

        /// <summary>Gets the applied policy, including local fail-safe holds.</summary>
        public string ActivePolicyName => _holding ? nameof(EmergencyHoldPolicy) : _localPolicy?.PolicyName ?? _applied?.PolicyName ?? nameof(EmergencyHoldPolicy);
        /// <summary>Gets the selected local baseline name even while safety logic is holding agents.</summary>
        public string SelectedLocalPolicyName => _localPolicy?.PolicyName ?? string.Empty;
        /// <summary>Gets whether this Unity process is currently executing a local built-in mission.</summary>
        public bool IsLocalMissionSelected => _localPolicy != null;
        /// <summary>Gets whether a local mission currently has fresh perception and non-hold directives.</summary>
        public bool IsLocalMissionMoving => _localPolicy != null && !_holding;
        /// <summary>Gets whether a post-mission remote Brain response passed stream and telemetry validation.</summary>
        public bool IsVerifiedBrainPolicyActive => _localPolicy == null && _applied != null && _currentMission != null &&
            _applied.BrainStatus == "active" && _applied.TimestampUnixMs >= _currentMission.IssuedAtUnixMs;
        /// <summary>Gets the truthful local execution or hold reason for the HUD.</summary>
        public string LocalExecutionStatus => _localExecutionStatus;
        /// <summary>Gets latest bounded server diagnostics, even before a perception snapshot is available.</summary>
        public SwarmControlSnapshot Control => _client?.GetView().Control;
        /// <summary>Gets the latest operator mission used to annotate tactical geometry.</summary>
        public SwarmMissionCommand CurrentMission => _currentMission;
        /// <summary>Gets a safe reason for the current local hold.</summary>
        public string FailureCategory => _localFailure ?? _client?.GetView().FailureCategory ?? Control?.PerceptionFailureCategory;
        /// <summary>Gets the configured real simulation entities.</summary>
        public IReadOnlyList<SwarmAgentController> Agents => _agentView ?? (IReadOnlyList<SwarmAgentController>)agents;

        /// <summary>Wires explicit references during Editor scene construction.</summary>
        public void Configure(TwinWebSocketClient connection, SwarmAgentController[] entities)
        { stream = connection; agents = (SwarmAgentController[])entities.Clone(); _agentView = Array.AsReadOnly(agents); }

        private void OnEnable()
        {
            _agentView ??= Array.AsReadOnly(agents);
            ApplyCommandLineBackendOrigin();
            if (stream == null || agents.Length == 0 || agents.Length > 128 || !Uri.TryCreate(backendOrigin, UriKind.Absolute, out var origin))
                throw new InvalidOperationException("Assign a world stream, local origin, and bounded authored agent set.");
            var ids = new HashSet<int>(); foreach (var agent in agents) if (agent == null || !ids.Add(agent.AgentId)) throw new InvalidOperationException("Agent IDs must be unique.");
            _session = Guid.NewGuid(); _sequence = 0; _publishAt = 0; _applied = null; _currentMission = null; _holding = true;
            _localPolicy = null; _localMissionUsesBrain = false; _missionTelemetrySequence = 0;
            _operatorHazards.Clear(); _operatorHazardSessionId = Guid.Empty;
            _localExecutionStatus = "Waiting for a current observation";
            _spawnRecoveryAttempted.Clear();
            _client = new SwarmControlClient(); _stop = new CancellationTokenSource();
            var client = _client; var stop = _stop;
            _worker = Task.Run(() => client.RunAsync(origin, stop.Token));
        }

        /// <summary>Queues a bounded local mission, optionally requesting explicitly selected model inference.</summary>
        public void RequestMission(SwarmMissionKind kind, string instruction, bool useBrain = false, float radiusMeters = 3)
        {
            if (!isActiveAndEnabled) throw new InvalidOperationException("Swarm control is inactive.");
            var command = new SwarmMissionCommand(Guid.NewGuid(), kind, instruction,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null, radiusMeters);
            _currentMission = command;
            _localMissionUsesBrain = useBrain;
            _missionTelemetrySequence = _sequence + 1;
            _localFailure = null;
            _localPolicy = CreateBuiltInPolicy(kind, radiusMeters, useBrain);
            _applied = null;
            _localExecutionStatus = kind == SwarmMissionKind.HoldPosition
                ? "LOCAL HOLD active · all agents stopped"
                : "LOCAL " + _localPolicy.PolicyName + " selected · checking current perception";
            if (kind == SwarmMissionKind.HoldPosition)
                HoldAll();

            // Queue the same command to the backend when it is available. Network failure does not
            // undo the local built-in policy selected above.
            if (_client != null)
            {
                try { _client.QueueMission(new SwarmMissionRequest(command, useBrain)); }
                catch (InvalidOperationException) { _localExecutionStatus = "LOCAL " + _localPolicy.PolicyName + " · backend unavailable"; }
            }
        }

        /// <summary>Applies a local hazard flag to a currently observed track for local planning and scene presentation.</summary>
        public bool SetLocalHazardOverride(int trackId, bool isHazard)
        {
            if (stream == null || !stream.TryGetCurrentView(out var view) || view.State.Message == null) return false;
            var message = view.State.Message;
            var found = false;
            foreach (var track in message.World.Obstacles)
                if (track.Obstacle.Id == trackId && view.IsTrackCurrent(track)) { found = true; break; }
            if (!found) return false;
            if (_operatorHazardSessionId != message.World.SessionId)
            {
                _operatorHazards.Clear();
                _operatorHazardSessionId = message.World.SessionId;
            }
            _operatorHazards[trackId] = isHazard;
            return true;
        }

        private void Update()
        {
            if (_worker != null && _worker.IsFaulted) _localFailure = "control_worker_failed";
            if (_client != null && Time.unscaledTime >= _publishAt)
            {
                _publishAt = Time.unscaledTime + 0.1f;
                var telemetry = new AgentTelemetry[agents.Length]; for (var i = 0; i < agents.Length; i++) telemetry[i] = agents[i].ReadTelemetry();
                _client.PublishTelemetry(new SwarmTelemetrySnapshot(_session, _sequence++, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), telemetry));
            }
            if (_localPolicy != null)
            {
                if (TryPromoteValidatedRemotePolicy())
                {
                    _localPolicy = null;
                    _localExecutionStatus = "Validated backend directives active";
                }
                else
                {
                    ApplyLocalPolicy();
                    return;
                }
            }
            if (_client == null) { HoldAll(); return; }
            var view = _client.GetView(); var control = view.Control;
            if (!stream.TryGetCurrentView(out var worldView) || !view.HasFreshReply || control == null || !control.IsSourceCurrent ||
                control.TelemetrySessionId != _session || control.PerceptionStreamId != worldView.State.Message.StreamId ||
                control.CalibrationId != worldView.State.Message.World.CalibrationId)
            { _localFailure = control?.PerceptionFailureCategory ?? view.FailureCategory ?? "observation_not_current"; HoldAll(); return; }
            _localFailure = null;
            _obstacles.Clear();
            _geometryBlockedAgents.Clear();
            foreach (var track in worldView.State.Message.World.Obstacles) _obstacles.Add(track.Obstacle);
            foreach (var directive in control.Directives)
            {
                var agent = FindAgent(directive.AgentId);
                if (agent == null)
                { _localFailure = "latest_geometry_blocks_directive"; HoldAll(); return; }
                if (directive.DesiredSpeed == 0 && directive.StatusTag == "blocked_hold" && !agent.HasReceivedMovementDirective &&
                    !_spawnRecoveryAttempted.Contains(agent.AgentId) && !SafeNavigation.IsFree(agent.PlanarPosition, control.Bounds, _obstacles))
                {
                    _spawnRecoveryAttempted.Add(agent.AgentId);
                    if (SafeNavigation.TryFindNearestFreePoint(agent.PlanarPosition, control.Bounds, _obstacles, out var safeSpawn) &&
                        agent.TryRepositionAtSafeSpawn(safeSpawn))
                        Debug.Log("OmniTwin: repositioned agent " + agent.AgentId + " to a collision-free authored spawn before patrol.");
                }
                if (directive.DesiredSpeed > 0 && !SafeNavigation.SegmentIsFree(agent.PlanarPosition, directive.TargetPosition, control.Bounds, _obstacles))
                {
                    // The perception stream can be newer than the latest control reply while an
                    // object is moving. Hold only the route that now intersects geometry; stopping
                    // every unit here made a single moving obstacle appear to freeze the whole twin.
                    _localFailure = "latest_geometry_blocks_directive";
                    _geometryBlockedAgents.Add(agent.AgentId);
                    agent.Hold();
                }
            }
            foreach (var directive in control.Directives)
            {
                var agent = FindAgent(directive.AgentId);
                if (_geometryBlockedAgents.Contains(agent.AgentId)) continue;
                if (_holding || DirectiveNeedsApply(agent.CurrentDirective, directive)) agent.ApplyDirective(directive);
            }
            _applied = control;
            _holding = false;
        }

        private bool TryPromoteValidatedRemotePolicy()
        {
            if (_client == null || _localPolicy == null || !stream.TryGetCurrentView(out var worldView)) return false;
            var view = _client.GetView(); var control = view.Control;
            if (!view.HasFreshReply || control == null || !control.IsSourceCurrent ||
                control.TelemetrySessionId != _session || control.PerceptionStreamId != worldView.State.Message.StreamId ||
                control.CalibrationId != worldView.State.Message.World.CalibrationId ||
                control.TelemetrySequenceNumber < _missionTelemetrySequence || _currentMission == null ||
                control.TimestampUnixMs < _currentMission.IssuedAtUnixMs) return false;
            if (_localMissionUsesBrain && control.BrainStatus != "active") return false;
            if (!_localMissionUsesBrain && control.PolicyName != _localPolicy.PolicyName) return false;
            return true;
        }

        private void ApplyLocalPolicy()
        {
            if (_localPolicy == null) return;
            if (_localPolicy is EmergencyHoldPolicy)
            {
                HoldAll();
                _localExecutionStatus = "LOCAL HOLD active · all agents stopped";
                return;
            }
            if (!stream.TryGetCurrentView(out var worldView) || worldView.State.Message == null ||
                worldView.State.Message.SourceMode is not (PerceptionSourceMode.LiveCamera or PerceptionSourceMode.SyntheticTabletopReplay))
            {
                HoldAll();
                _localExecutionStatus = "LOCAL HOLD · waiting for a current perception source";
                return;
            }

            var message = worldView.State.Message;
            if (_operatorHazardSessionId != message.World.SessionId)
            {
                _operatorHazards.Clear();
                _operatorHazardSessionId = message.World.SessionId;
            }
            _obstacles.Clear();
            foreach (var track in message.World.Obstacles)
                if (worldView.IsTrackCurrent(track))
                {
                    var obstacle = track.Obstacle;
                    if (_operatorHazards.TryGetValue(obstacle.Id, out var hazard) && obstacle.IsHazardZone != hazard)
                        obstacle = new SegmentedObstacle(obstacle.Id, obstacle.Label, obstacle.WorldPosition,
                            obstacle.RadiusMeters, obstacle.Confidence, hazard);
                    _obstacles.Add(obstacle);
                }
            if (_localPolicy is HazardQuarantinePolicy && _obstacles.Count == 0)
            {
                HoldAll();
                _localExecutionStatus = "LOCAL HOLD · cordon needs a current obstacle track";
                return;
            }

            try
            {
                var telemetry = new AgentTelemetry[agents.Length];
                for (var i = 0; i < agents.Length; i++) telemetry[i] = agents[i].ReadTelemetry();
                var world = new WorldStateSnapshot(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _obstacles,
                    telemetry, _localPolicy.PolicyName, _currentMission?.Instruction ?? string.Empty,
                    message.World.SequenceNumber);
                var directives = SafeNavigation.ValidateDirectives(world,
                    _localPolicy.ComputeDirectives(world, Time.timeSinceLevelLoad), WorkspaceBounds.TacticalDesk);
                var movingAgents = 0;
                var blockedAgents = 0;
                foreach (var directive in directives)
                {
                    var agent = FindAgent(directive.AgentId);
                    if (agent == null) throw new InvalidOperationException("local_policy_agent_missing");
                    if (directive.DesiredSpeed > 0 && !SafeNavigation.SegmentIsFree(agent.PlanarPosition,
                        directive.TargetPosition, WorkspaceBounds.TacticalDesk, _obstacles))
                    {
                        agent.Hold();
                        blockedAgents++;
                        continue;
                    }
                    if (_holding || DirectiveNeedsApply(agent.CurrentDirective, directive)) agent.ApplyDirective(directive);
                    if (directive.DesiredSpeed > 0) movingAgents++;
                }
                _holding = movingAgents == 0;
                _localFailure = null;
                _localExecutionStatus = movingAgents > 0
                    ? "LOCAL " + _localPolicy.PolicyName + " active · " + movingAgents + " agents navigating · current geometry checked"
                    : "LOCAL HOLD · no safe movement directive is currently available";
                if (blockedAgents > 0) _localExecutionStatus += " · " + blockedAgents + " route(s) blocked";
            }
            catch (Exception error) when (error is ArgumentException || error is InvalidOperationException)
            {
                HoldAll();
                _localFailure = "local_policy_rejected";
                _localExecutionStatus = "LOCAL HOLD · built-in policy rejected unsafe directives";
            }
        }

        private static ISwarmPolicy CreateBuiltInPolicy(SwarmMissionKind kind, float radiusMeters, bool useBrain) => kind switch
        {
            SwarmMissionKind.HoldPosition => new EmergencyHoldPolicy(),
            SwarmMissionKind.PatrolPerimeter => new PerimeterPatrolPolicy(WorkspaceBounds.TacticalDesk),
            SwarmMissionKind.HazardQuarantine when useBrain => new HybridCordonPatrolBaselinePolicy(WorkspaceBounds.TacticalDesk, radiusMeters),
            SwarmMissionKind.HazardQuarantine => new HazardQuarantinePolicy(WorkspaceBounds.TacticalDesk, radiusMeters),
            SwarmMissionKind.GridSearchAndRescue => new GridSearchAndRescuePolicy(WorkspaceBounds.TacticalDesk),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        private static bool DirectiveNeedsApply(AgentTargetDirective current, AgentTargetDirective next)
        {
            if (current == null) return true;
            var dx = current.TargetPosition.X - next.TargetPosition.X;
            var dz = current.TargetPosition.Z - next.TargetPosition.Z;
            return dx * dx + dz * dz >= 0.04f || Mathf.Abs(current.DesiredSpeed - next.DesiredSpeed) >= 0.05f ||
                !string.Equals(current.StatusTag, next.StatusTag, StringComparison.Ordinal);
        }
        private SwarmAgentController FindAgent(int id) { foreach (var agent in agents) if (agent.AgentId == id) return agent; return null; }
        private void HoldAll() { foreach (var agent in agents) if (agent != null) agent.Hold(); _holding = true; }
        private void ApplyCommandLineBackendOrigin()
        {
            const string prefix = "--omnitwin-backend-origin=";
            var arguments = Environment.GetCommandLineArgs();
            for (var i = 0; i < arguments.Length; i++)
            {
                if (!arguments[i].StartsWith(prefix, StringComparison.Ordinal)) continue;
                var value = arguments[i].Substring(prefix.Length).Trim('"');
                if (!Uri.TryCreate(value, UriKind.Absolute, out var address) || address.Scheme != "http" ||
                    !(address.Host == "127.0.0.1" || address.Host == "localhost") || address.AbsolutePath != "/" ||
                    address.Query.Length != 0 || address.Fragment.Length != 0 || address.UserInfo.Length != 0)
                    throw new InvalidOperationException("The standalone backend override must be an HTTP loopback origin.");
                backendOrigin = address.GetLeftPart(UriPartial.Authority);
                return;
            }
        }
        private void OnDisable()
        {
            HoldAll(); var stop = _stop; var worker = _worker; var client = _client;
            _stop = null; _worker = null; _client = null; if (stop == null) return; stop.Cancel();
            if (worker == null) { client.Dispose(); stop.Dispose(); return; }
            _ = worker.ContinueWith(completed => { var error = completed.Exception; client.Dispose(); stop.Dispose(); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
