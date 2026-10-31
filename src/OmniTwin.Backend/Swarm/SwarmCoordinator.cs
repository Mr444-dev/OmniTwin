using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OmniTwin.Backend.Services;
using OmniTwin.Backend.Streaming;
using OmniTwin.Contracts;
using OmniTwin.Perception.Tracking;
using OmniTwin.Policies;

namespace OmniTwin.Backend.Swarm;

/// <summary>Supervises measured agent sessions, built-in missions, isolated policies, and source-loss holds.</summary>
public sealed class SwarmCoordinator : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _evaluation = new(1, 1);
    private readonly SnapshotBroker _broker;
    private readonly BackendRuntimeState _runtime;
    private readonly TimeProvider _time;
    private readonly Lazy<Task<CalibrationProfile>>? _profile;
    private WorkspaceBounds _bounds = WorkspaceBounds.TacticalDesk;
    private ISwarmPolicy _baseline;
    private ISupervisedSwarmPolicy? _generated;
    private SwarmMissionCommand _mission;
    private Guid _clientSession;
    private long _lastSequence = -1, _lastTelemetryTimestamp;
    private SwarmTelemetrySnapshot? _telemetry;
    private SwarmControlSnapshot? _control;
    private string _brainStatus = "baseline";
    private string? _brainDiagnostic, _model;
    private bool _modelFallback;
    private int _attempts;
    private IReadOnlyList<string> _diagnostics = Array.Empty<string>();
    private readonly long _started;
    private Func<SwarmMissionRequest, CancellationToken, Task>? _brainHandler;
    private readonly Dictionary<Guid, SwarmMissionRequest> _commands = new();
    private readonly Queue<Guid> _commandOrder = new();

    /// <summary>Creates a coordinator. No hardware, provider call, or generated code starts in the constructor.</summary>
    public SwarmCoordinator(SnapshotBroker broker, BackendRuntimeState runtime, BackendOptions options, TimeProvider time)
    {
        _broker = broker; _runtime = runtime; _time = time; _started = time.GetTimestamp();
        _mission = new SwarmMissionCommand(Guid.NewGuid(), SwarmMissionKind.PatrolPerimeter, "Patrol workspace perimeter", time.GetUtcNow().ToUnixTimeMilliseconds(), null, 3);
        _baseline = new PerimeterPatrolPolicy(_bounds);
        if (options.EnablePerception) _profile = new Lazy<Task<CalibrationProfile>>(() => CalibrationProfile.LoadAsync(options.CalibrationPath!));
    }

    /// <summary>Gets the latest owned telemetry/control pair for diagnostics.</summary>
    public (SwarmTelemetrySnapshot? Telemetry, SwarmControlSnapshot? Control) Read() { lock (_gate) return (_telemetry, _control); }
    /// <summary>Gets current calibrated planning bounds as an immutable value.</summary>
    public WorkspaceBounds Bounds { get { lock (_gate) return _bounds; } }

    /// <summary>Registers the model-synthesis service used for explicitly requested model missions.</summary>
    public void RegisterBrain(Func<SwarmMissionRequest, CancellationToken, Task> handler)
    { ArgumentNullException.ThrowIfNull(handler); lock (_gate) _brainHandler = handler; }

    /// <summary>Selects the safe baseline immediately and dispatches optional synthesis without stopping simulation.</summary>
    public async Task SubmitMissionAsync(SwarmMissionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Func<SwarmMissionRequest, CancellationToken, Task>? handler;
        lock (_gate)
        {
            if (_commands.TryGetValue(request.Command.CommandId, out var accepted))
            {
                if (accepted.Command != request.Command || accepted.UseBrain != request.UseBrain) throw new ArgumentException("Conflicting command identity.");
                return;
            }
            _commands.Add(request.Command.CommandId, request); _commandOrder.Enqueue(request.Command.CommandId);
            if (_commandOrder.Count > 128) _commands.Remove(_commandOrder.Dequeue());
            SelectMission(request.Command); handler = _brainHandler;
        }
        if (handler is null)
        { if (request.UseBrain) ReportBrain(request.Command.CommandId, "fallback", "brain_unavailable", null, false, 0, Array.Empty<string>()); return; }
        await handler(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Selects a real built-in mission and disposes any superseded isolated worker.</summary>
    public void SelectMission(SwarmMissionCommand mission)
    {
        ArgumentNullException.ThrowIfNull(mission); ISupervisedSwarmPolicy? old;
        lock (_gate)
        {
            _mission = mission; _baseline = Baseline(mission, _bounds); old = _generated; _generated = null;
            _brainStatus = "baseline"; _brainDiagnostic = null; _model = null; _modelFallback = false; _attempts = 0; _diagnostics = Array.Empty<string>();
        }
        (old as IDisposable)?.Dispose();
    }

    /// <summary>Publishes bounded synthesis diagnostics only while that command remains current.</summary>
    public bool ReportBrain(Guid commandId, string phase, string? category, string? model, bool modelFallback, int attempts, IReadOnlyList<string> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(phase) || phase.Length > 128 || category?.Length > 128 || model?.Length > 200 || attempts < 0 || attempts > 3 || diagnostics.Count > 20)
            throw new ArgumentException("Unbounded brain diagnostic.");
        var safeDiagnostics = new List<string>(diagnostics.Count);
        foreach (var item in diagnostics) safeDiagnostics.Add(item.Length <= 512 ? item : item.Substring(0, 512));
        lock (_gate)
        {
            if (commandId != _mission.CommandId) return false;
            _brainStatus = phase; _brainDiagnostic = category; _model = model; _modelFallback = modelFallback; _attempts = attempts;
            _diagnostics = safeDiagnostics.AsReadOnly(); return true;
        }
    }

    /// <summary>Activates a fully verified isolated worker if no newer command has replaced it.</summary>
    public bool Activate(Guid commandId, ISupervisedSwarmPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy); ISupervisedSwarmPolicy? old;
        lock (_gate) { if (commandId != _mission.CommandId) return false; old = _generated; _generated = policy; _brainStatus = "active"; _brainDiagnostic = null; }
        (old as IDisposable)?.Dispose(); return true;
    }

    /// <summary>Evaluates bounded, current telemetry; stale perception always yields an immediate zero-speed hold.</summary>
    public async Task<SwarmControlSnapshot> EvaluateAsync(SwarmTelemetrySnapshot telemetry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        var evaluationStarted = _time.GetTimestamp();
        await _evaluation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
            if (now - telemetry.TimestampUnixMs > 2000 || telemetry.TimestampUnixMs - now > 500)
                throw new ArgumentException("telemetry_stale_or_future");
            lock (_gate)
            {
                if (_clientSession != telemetry.SessionId)
                {
                    if (_clientSession != Guid.Empty && _time.GetElapsedTime(_lastTelemetryTimestamp).TotalMilliseconds < 2500)
                        throw new InvalidOperationException("telemetry_session_in_use");
                    _clientSession = telemetry.SessionId; _lastSequence = -1;
                }
                if (telemetry.SequenceNumber < _lastSequence ||
                    (telemetry.SequenceNumber == _lastSequence && !SameTelemetry(telemetry, _telemetry)))
                    throw new InvalidOperationException("telemetry_out_of_order");
                _lastSequence = telemetry.SequenceNumber; _lastTelemetryTimestamp = _time.GetTimestamp(); _telemetry = telemetry;
            }
            var frame = _broker.Latest(); var source = _runtime.Read();
            var current = source.IsObservationCurrent;
            CalibrationProfile? profile = null;
            if (current && _profile is not null)
            {
                profile = await _profile.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
                current = frame is not null && frame.Message.World.CalibrationId == profile.CalibrationId;
                var corners = profile.Workspace.WorldCorners;
                var minX = float.MaxValue; var maxX = float.MinValue; var minZ = float.MaxValue; var maxZ = float.MinValue;
                foreach (var corner in corners) { minX = Math.Min(minX, corner.X); maxX = Math.Max(maxX, corner.X); minZ = Math.Min(minZ, corner.Z); maxZ = Math.Max(maxZ, corner.Z); }
                var bounds = new WorkspaceBounds(minX, maxX, minZ, maxZ);
                lock (_gate) if (_bounds != bounds) { _bounds = bounds; _baseline = Baseline(_mission, bounds); }
            }
            ISwarmPolicy baseline; ISupervisedSwarmPolicy? generated; SwarmMissionCommand mission; WorkspaceBounds workspace;
            lock (_gate) { baseline = _baseline; generated = _generated; mission = _mission; workspace = _bounds; }
            var obstacles = current && frame is not null ? Obstacles(frame.Message.World) : Array.Empty<SegmentedObstacle>();
            var world = new WorldStateSnapshot(now, obstacles, telemetry.Agents, generated?.PolicyName ?? baseline.PolicyName, mission.Instruction, telemetry.SequenceNumber);
            var seconds = (float)_time.GetElapsedTime(_started).TotalSeconds;
            IReadOnlyList<AgentTargetDirective> directives;
            var policyName = current ? generated?.PolicyName ?? baseline.PolicyName : nameof(EmergencyHoldPolicy);
            if (!current) directives = new EmergencyHoldPolicy().ComputeDirectives(world, seconds);
            else
            {
                try
                {
                    directives = generated is null ? baseline.ComputeDirectives(world, seconds) :
                        await generated.ComputeAsync(world, seconds, cancellationToken).ConfigureAwait(false);
                    directives = SafeNavigation.ValidateDirectives(world, directives, workspace);
                    if (profile is not null)
                        foreach (var directive in directives)
                            if (directive.DesiredSpeed > 0 && !profile.Workspace.ContainsCircle(directive.TargetPosition, SafeNavigation.ClearanceMeters))
                                throw new PolicyExecutionException("target_outside_calibrated_polygon");
                }
                catch (Exception error) when (error is PolicyExecutionException or ArgumentException)
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(generated, _generated))
                        { _generated = null; _brainStatus = "fallback"; _brainDiagnostic = error is PolicyExecutionException failure ? failure.Category : "invalid_directives"; }
                    }
                    (generated as IDisposable)?.Dispose();
                    directives = baseline.ComputeDirectives(world, seconds); policyName = baseline.PolicyName;
                    directives = SafeNavigation.ValidateDirectives(world, directives, workspace);
                    if (profile is not null)
                        foreach (var directive in directives)
                            if (directive.DesiredSpeed > 0 && !profile.Workspace.ContainsCircle(directive.TargetPosition, SafeNavigation.ClearanceMeters))
                            { directives = new EmergencyHoldPolicy().ComputeDirectives(world, seconds); policyName = nameof(EmergencyHoldPolicy); break; }
                }
            }
            lock (_gate)
            {
                if (_mission.CommandId != mission.CommandId)
                { current = false; directives = new EmergencyHoldPolicy().ComputeDirectives(world, seconds); policyName = nameof(EmergencyHoldPolicy); }
                _control = new SwarmControlSnapshot(telemetry.SessionId, telemetry.SequenceNumber, _time.GetUtcNow().ToUnixTimeMilliseconds(), current,
                    frame?.Message.StreamId, frame?.Message.World.CalibrationId, frame?.Message.SequenceNumber, workspace, policyName,
                    source.Reason ?? (current ? null : "observation_not_current"), _brainStatus, _brainDiagnostic, _model, _modelFallback, _attempts, _diagnostics, directives,
                    Math.Clamp(_time.GetElapsedTime(evaluationStarted, _time.GetTimestamp()).TotalMilliseconds, 0, 60_000));
                return _control;
            }
        }
        finally { _evaluation.Release(); }
    }

    /// <summary>Builds an owned model context from actual telemetry and current observed footprints.</summary>
    public WorldStateSnapshot BuildWorld()
    {
        lock (_gate)
        {
            var frame = _broker.Latest();
            return new WorldStateSnapshot(_time.GetUtcNow().ToUnixTimeMilliseconds(), frame is null ? Array.Empty<SegmentedObstacle>() : Obstacles(frame.Message.World),
                _telemetry?.Agents ?? Array.Empty<AgentTelemetry>(), _baseline.PolicyName, _mission.Instruction, _telemetry?.SequenceNumber ?? 0);
        }
    }
    private static IReadOnlyList<SegmentedObstacle> Obstacles(ObstacleTrackingSnapshot snapshot)
    { var result = new List<SegmentedObstacle>(snapshot.Obstacles.Count); foreach (var track in snapshot.Obstacles) result.Add(track.Obstacle); return result.AsReadOnly(); }
    private static bool SameTelemetry(SwarmTelemetrySnapshot value, SwarmTelemetrySnapshot? other)
    {
        if (other is null || value.SessionId != other.SessionId || value.TimestampUnixMs != other.TimestampUnixMs || value.Agents.Count != other.Agents.Count) return false;
        for (var i = 0; i < value.Agents.Count; i++) if (value.Agents[i] != other.Agents[i]) return false;
        return true;
    }
    private static ISwarmPolicy Baseline(SwarmMissionCommand mission, WorkspaceBounds bounds) => mission.Kind switch
    {
        SwarmMissionKind.HoldPosition => new EmergencyHoldPolicy(),
        SwarmMissionKind.PatrolPerimeter => new PerimeterPatrolPolicy(bounds),
        SwarmMissionKind.HazardQuarantine => new HazardQuarantinePolicy(bounds, mission.SafetyRadiusMeters),
        SwarmMissionKind.GridSearchAndRescue => new GridSearchAndRescuePolicy(bounds),
        _ => throw new ArgumentException("Unsupported mission.", nameof(mission))
    };
    /// <summary>Kills any owned isolated policy worker on host shutdown.</summary>
    public void Dispose() { lock (_gate) { (_generated as IDisposable)?.Dispose(); _generated = null; } _evaluation.Dispose(); }
}
