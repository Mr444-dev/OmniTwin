using System;
using System.Collections.Generic;

namespace OmniTwin.Contracts;

/// <summary>Owned measured simulation telemetry with an explicit client session and sequence.</summary>
public sealed class SwarmTelemetrySnapshot
{
    /// <summary>Creates bounded telemetry; positions and velocities are validated by their value types.</summary>
    public SwarmTelemetrySnapshot(Guid sessionId, long sequenceNumber, long timestampUnixMs, IReadOnlyList<AgentTelemetry> agents)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("A client session is required.", nameof(sessionId));
        if (agents is null || agents.Count > 128) throw new ArgumentException("At most 128 measured agents are accepted.", nameof(agents));
        SessionId = sessionId; SequenceNumber = ContractGuard.Nonnegative(sequenceNumber, nameof(sequenceNumber));
        TimestampUnixMs = ContractGuard.Nonnegative(timestampUnixMs, nameof(timestampUnixMs));
        Agents = ContractGuard.CopyUnique(agents, agent => agent.AgentId, nameof(agents));
    }
    /// <summary>Gets the telemetry schema.</summary>
    public int SchemaVersion => 2;
    /// <summary>Gets the client's run identity.</summary>
    public Guid SessionId { get; }
    /// <summary>Gets its monotonic publication sequence.</summary>
    public long SequenceNumber { get; }
    /// <summary>Gets capture wall-clock time.</summary>
    public long TimestampUnixMs { get; }
    /// <summary>Gets the owned simulation measurements.</summary>
    public IReadOnlyList<AgentTelemetry> Agents { get; }
}

/// <summary>An operator command with explicit permission to invoke model synthesis.</summary>
public sealed class SwarmMissionRequest
{
    /// <summary>Creates a request; inference is enabled only by an explicit true flag.</summary>
    public SwarmMissionRequest(SwarmMissionCommand command, bool useBrain)
    { Command = command ?? throw new ArgumentNullException(nameof(command)); UseBrain = useBrain; }
    /// <summary>Gets the typed command and operator instruction.</summary>
    public SwarmMissionCommand Command { get; }
    /// <summary>Gets whether this command requests paid model inference.</summary>
    public bool UseBrain { get; }
}

/// <summary>A complete control reply correlated to measured telemetry and the perception session.</summary>
public sealed class SwarmControlSnapshot
{
    /// <summary>Creates a bounded immutable reply. A source-invalid reply can only contain zero-speed directives.</summary>
    public SwarmControlSnapshot(Guid telemetrySessionId, long telemetrySequenceNumber, long timestampUnixMs,
        bool isSourceCurrent, Guid? perceptionStreamId, Guid? calibrationId, long? worldSequenceNumber, WorkspaceBounds bounds,
        string policyName, string? perceptionFailureCategory, string brainStatus, string? brainDiagnostic,
        string? modelIdUsed, bool usedModelFallback, int generationAttempts, IReadOnlyList<string> compilationDiagnostics,
        IReadOnlyList<AgentTargetDirective> directives, double evaluationDurationMilliseconds = 0)
    {
        if (telemetrySessionId == Guid.Empty) throw new ArgumentException("Telemetry session is required.", nameof(telemetrySessionId));
        if (perceptionStreamId == Guid.Empty || calibrationId == Guid.Empty ||
            (isSourceCurrent && (!perceptionStreamId.HasValue || !calibrationId.HasValue || !worldSequenceNumber.HasValue)))
            throw new ArgumentException("Current controls require perception identity.");
        if (worldSequenceNumber.HasValue) ContractGuard.Nonnegative(worldSequenceNumber.Value, nameof(worldSequenceNumber));
        bounds.Clamp(default, 0.7f);
        TelemetrySessionId = telemetrySessionId; TelemetrySequenceNumber = ContractGuard.Nonnegative(telemetrySequenceNumber, nameof(telemetrySequenceNumber));
        TimestampUnixMs = ContractGuard.Nonnegative(timestampUnixMs, nameof(timestampUnixMs)); IsSourceCurrent = isSourceCurrent;
        PerceptionStreamId = perceptionStreamId; CalibrationId = calibrationId; WorldSequenceNumber = worldSequenceNumber; Bounds = bounds;
        PolicyName = ContractGuard.Text(policyName, 128, nameof(policyName));
        PerceptionFailureCategory = Optional(perceptionFailureCategory); BrainStatus = ContractGuard.Text(brainStatus, 64, nameof(brainStatus));
        BrainDiagnostic = Optional(brainDiagnostic); ModelIdUsed = Optional(modelIdUsed); UsedModelFallback = usedModelFallback;
        if (usedModelFallback && modelIdUsed is null) throw new ArgumentException("Model fallback requires an identified model.");
        if (generationAttempts < 0 || generationAttempts > 3) throw new ArgumentOutOfRangeException(nameof(generationAttempts));
        GenerationAttempts = generationAttempts;
        if (!double.IsFinite(evaluationDurationMilliseconds) || evaluationDurationMilliseconds < 0 || evaluationDurationMilliseconds > 60_000)
            throw new ArgumentOutOfRangeException(nameof(evaluationDurationMilliseconds));
        EvaluationDurationMilliseconds = evaluationDurationMilliseconds;
        if (compilationDiagnostics is null || compilationDiagnostics.Count > 20) throw new ArgumentException("At most twenty diagnostics are allowed.");
        var messages = new string[compilationDiagnostics.Count];
        for (var i = 0; i < messages.Length; i++) messages[i] = ContractGuard.Text(compilationDiagnostics[i], 512, nameof(compilationDiagnostics));
        CompilationDiagnostics = Array.AsReadOnly(messages);
        if (directives is null || directives.Count > 128) throw new ArgumentException("At most 128 directives are allowed.");
        Directives = ContractGuard.CopyUnique(directives, directive => directive.AgentId, nameof(directives));
        foreach (var directive in Directives)
            if ((!isSourceCurrent && directive.DesiredSpeed != 0) || directive.DesiredSpeed > 4)
                throw new ArgumentException("Untrusted sources must hold; movement speeds are bounded.");
    }
    private static string? Optional(string? value) => value is null ? null : ContractGuard.Text(value, 128, nameof(value));
    /// <summary>Gets the control schema.</summary>
    public int SchemaVersion => 2;
    /// <summary>Gets the client's run identity.</summary>
    public Guid TelemetrySessionId { get; }
    /// <summary>Gets the telemetry publication evaluated by the server.</summary>
    public long TelemetrySequenceNumber { get; }
    /// <summary>Gets server publication wall-clock time.</summary>
    public long TimestampUnixMs { get; }
    /// <summary>Gets whether all source and safety gates accepted movement.</summary>
    public bool IsSourceCurrent { get; }
    /// <summary>Gets the backend perception run identity.</summary>
    public Guid? PerceptionStreamId { get; }
    /// <summary>Gets the calibration identity used for planning.</summary>
    public Guid? CalibrationId { get; }
    /// <summary>Gets the perception publication used for planning.</summary>
    public long? WorldSequenceNumber { get; }
    /// <summary>Gets authoritative workspace limits.</summary>
    public WorkspaceBounds Bounds { get; }
    /// <summary>Gets the policy actually producing these directives.</summary>
    public string PolicyName { get; }
    /// <summary>Gets a sanitized perception reason, including authentication/access failures.</summary>
    public string? PerceptionFailureCategory { get; }
    /// <summary>Gets the synthesis/execution phase.</summary>
    public string BrainStatus { get; }
    /// <summary>Gets a sanitized generation or execution reason.</summary>
    public string? BrainDiagnostic { get; }
    /// <summary>Gets the model identifier actually requested, or null without inference.</summary>
    public string? ModelIdUsed { get; }
    /// <summary>Gets whether the dated model returned 404 and the official alias was used.</summary>
    public bool UsedModelFallback { get; }
    /// <summary>Gets the bounded number of generation/verification attempts.</summary>
    public int GenerationAttempts { get; }
    /// <summary>Gets measured end-to-end backend evaluation duration, including the isolated worker exchange.</summary>
    public double EvaluationDurationMilliseconds { get; }
    /// <summary>Gets bounded compiler/verification diagnostics.</summary>
    public IReadOnlyList<string> CompilationDiagnostics { get; }
    /// <summary>Gets the owned target directives.</summary>
    public IReadOnlyList<AgentTargetDirective> Directives { get; }
}
