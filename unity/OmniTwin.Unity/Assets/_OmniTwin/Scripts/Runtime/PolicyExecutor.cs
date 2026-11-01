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
        private IReadOnlyList<SwarmAgentController> _agentView;
        private bool _holding = true;
        private string _localFailure;
        private readonly List<SegmentedObstacle> _obstacles = new List<SegmentedObstacle>();
        private readonly HashSet<int> _spawnRecoveryAttempted = new HashSet<int>();

        /// <summary>Gets the applied policy, including local fail-safe holds.</summary>
        public string ActivePolicyName => _holding ? nameof(EmergencyHoldPolicy) : _applied?.PolicyName ?? nameof(EmergencyHoldPolicy);
        /// <summary>Gets latest bounded server diagnostics, even before a perception snapshot is available.</summary>
        public SwarmControlSnapshot Control => _client?.GetView().Control;
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
            _session = Guid.NewGuid(); _sequence = 0; _publishAt = 0; _applied = null; _holding = true;
            _spawnRecoveryAttempted.Clear();
            _client = new SwarmControlClient(); _stop = new CancellationTokenSource();
            var client = _client; var stop = _stop;
            _worker = Task.Run(() => client.RunAsync(origin, stop.Token));
        }

        /// <summary>Queues a bounded local mission, optionally requesting explicitly selected model inference.</summary>
        public void RequestMission(SwarmMissionKind kind, string instruction, bool useBrain = false, float radiusMeters = 3)
        {
            if (!isActiveAndEnabled || _client == null) throw new InvalidOperationException("Swarm control is inactive.");
            _client.QueueMission(new SwarmMissionRequest(new SwarmMissionCommand(Guid.NewGuid(), kind, instruction,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null, radiusMeters), useBrain));
        }

        private void Update()
        {
            if (_worker != null && _worker.IsFaulted) { _localFailure = "control_worker_failed"; HoldAll(); return; }
            if (Time.unscaledTime >= _publishAt)
            {
                _publishAt = Time.unscaledTime + 0.1f;
                var telemetry = new AgentTelemetry[agents.Length]; for (var i = 0; i < agents.Length; i++) telemetry[i] = agents[i].ReadTelemetry();
                _client.PublishTelemetry(new SwarmTelemetrySnapshot(_session, _sequence++, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), telemetry));
            }
            var view = _client.GetView(); var control = view.Control;
            if (!stream.TryGetCurrentView(out var worldView) || !view.HasFreshReply || control == null || !control.IsSourceCurrent ||
                control.TelemetrySessionId != _session || control.PerceptionStreamId != worldView.State.Message.StreamId ||
                control.CalibrationId != worldView.State.Message.World.CalibrationId)
            { _localFailure = control?.PerceptionFailureCategory ?? view.FailureCategory ?? "observation_not_current"; HoldAll(); return; }
            _localFailure = null;
            _obstacles.Clear();
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
                { _localFailure = "latest_geometry_blocks_directive"; HoldAll(); return; }
            }
            if (!ReferenceEquals(_applied, control) || _holding)
            { foreach (var directive in control.Directives) FindAgent(directive.AgentId).ApplyDirective(directive); _applied = control; }
            _holding = false;
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
