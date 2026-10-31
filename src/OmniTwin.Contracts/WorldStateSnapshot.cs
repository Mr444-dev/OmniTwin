using System;
using System.Collections.Generic;

namespace OmniTwin.Contracts;

/// <summary>A validated immutable snapshot. Collection inputs are defensively copied.</summary>
public sealed record WorldStateSnapshot
{
    /// <summary>The only schema version supported by this assembly.</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>Creates a snapshot; collection IDs must be unique and timestamps nonnegative.</summary>
    public WorldStateSnapshot(long timestampUnixMs, IReadOnlyList<SegmentedObstacle> obstacles,
        IReadOnlyList<AgentTelemetry> agents, string activePolicyName, string lastVoiceCommand,
        long sequenceNumber = 0, int schemaVersion = CurrentSchemaVersion)
    {
        if (schemaVersion != CurrentSchemaVersion) throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        if (lastVoiceCommand is null) throw new ArgumentNullException(nameof(lastVoiceCommand));
        if (lastVoiceCommand.Length > 4096) throw new ArgumentException("Command exceeds 4096 characters.", nameof(lastVoiceCommand));
        TimestampUnixMs = ContractGuard.Nonnegative(timestampUnixMs, nameof(timestampUnixMs));
        SequenceNumber = ContractGuard.Nonnegative(sequenceNumber, nameof(sequenceNumber));
        SchemaVersion = schemaVersion;
        Obstacles = ContractGuard.CopyUnique(obstacles, item => item.Id, nameof(obstacles));
        Agents = ContractGuard.CopyUnique(agents, item => item.AgentId, nameof(agents));
        ActivePolicyName = ContractGuard.Text(activePolicyName, 128, nameof(activePolicyName));
        LastVoiceCommand = lastVoiceCommand;
    }

    /// <summary>Gets the wire schema version.</summary>
    public int SchemaVersion { get; }
    /// <summary>Gets the monotonic sequence within a producer session; transport must check ordering.</summary>
    public long SequenceNumber { get; }
    /// <summary>Gets observation time in Unix milliseconds; this is not a monotonic clock.</summary>
    public long TimestampUnixMs { get; }
    /// <summary>Gets the owned read-only obstacle list.</summary>
    public IReadOnlyList<SegmentedObstacle> Obstacles { get; }
    /// <summary>Gets the owned read-only telemetry list.</summary>
    public IReadOnlyList<AgentTelemetry> Agents { get; }
    /// <summary>Gets the active policy's display name.</summary>
    public string ActivePolicyName { get; }
    /// <summary>Gets the last command, or an empty string before the first command.</summary>
    public string LastVoiceCommand { get; }
}
