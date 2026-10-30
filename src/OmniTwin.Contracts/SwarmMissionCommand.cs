using System;

namespace OmniTwin.Contracts;

/// <summary>A supported high-level mission category.</summary>
public enum SwarmMissionKind
{
    /// <summary>Stop movement while maintaining safe local control.</summary>
    HoldPosition,
    /// <summary>Patrol the configured workspace perimeter.</summary>
    PatrolPerimeter,
    /// <summary>Establish a safety perimeter around a hazard.</summary>
    HazardQuarantine,
    /// <summary>Search using an expanding spiral.</summary>
    SearchAndRescueSpiral
}

/// <summary>A typed mission request independent of any provider response format.</summary>
public sealed record SwarmMissionCommand
{
    /// <summary>Creates an identifiable mission with a positive safety radius in world meters.</summary>
    public SwarmMissionCommand(Guid commandId, SwarmMissionKind kind, string instruction,
        long issuedAtUnixMs, int? targetObstacleId, float safetyRadiusMeters)
    {
        if (commandId == Guid.Empty) throw new ArgumentException("Command ID cannot be empty.", nameof(commandId));
        if (!Enum.IsDefined(typeof(SwarmMissionKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (targetObstacleId.HasValue) ContractGuard.Id(targetObstacleId.Value, nameof(targetObstacleId));
        CommandId = commandId;
        Kind = kind;
        Instruction = ContractGuard.Text(instruction, 4096, nameof(instruction));
        IssuedAtUnixMs = ContractGuard.Nonnegative(issuedAtUnixMs, nameof(issuedAtUnixMs));
        TargetObstacleId = targetObstacleId;
        SafetyRadiusMeters = ContractGuard.Positive(safetyRadiusMeters, nameof(safetyRadiusMeters));
    }

    /// <summary>Gets the command correlation ID.</summary>
    public Guid CommandId { get; }
    /// <summary>Gets the typed mission.</summary>
    public SwarmMissionKind Kind { get; }
    /// <summary>Gets the operator instruction.</summary>
    public string Instruction { get; }
    /// <summary>Gets issue time in Unix milliseconds.</summary>
    public long IssuedAtUnixMs { get; }
    /// <summary>Gets the selected obstacle, or null for untargeted missions.</summary>
    public int? TargetObstacleId { get; }
    /// <summary>Gets the requested safety clearance in meters.</summary>
    public float SafetyRadiusMeters { get; }
}
