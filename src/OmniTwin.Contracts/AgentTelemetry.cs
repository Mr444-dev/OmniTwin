using System;

namespace OmniTwin.Contracts;

/// <summary>The physical navigation class of a simulated agent.</summary>
public enum AgentRole
{
    /// <summary>A ground vehicle constrained to its navigation surface.</summary>
    GroundVehicle,
    /// <summary>An aerial agent whose altitude is handled by the simulation layer.</summary>
    AerialDrone
}

/// <summary>An agent's reported operational state.</summary>
public enum AgentStatus
{
    /// <summary>No active movement directive.</summary>
    Idle,
    /// <summary>Moving toward a validated target.</summary>
    Navigating,
    /// <summary>Holding its current position.</summary>
    Holding,
    /// <summary>Battery reserve requires attention.</summary>
    LowBattery,
    /// <summary>Agent telemetry or control is unavailable.</summary>
    Offline
}

/// <summary>Immutable agent state with a normalized battery level.</summary>
public sealed record AgentTelemetry
{
    /// <summary>Creates telemetry; undefined enum values and battery levels outside [0, 1] are rejected.</summary>
    public AgentTelemetry(int agentId, AgentRole role, Vector2D currentPosition,
        Vector2D currentVelocity, float batteryLevel, AgentStatus status)
    {
        if (!Enum.IsDefined(typeof(AgentRole), role)) throw new ArgumentOutOfRangeException(nameof(role));
        if (!Enum.IsDefined(typeof(AgentStatus), status)) throw new ArgumentOutOfRangeException(nameof(status));
        AgentId = ContractGuard.Id(agentId, nameof(agentId));
        Role = role;
        CurrentPosition = currentPosition;
        CurrentVelocity = currentVelocity;
        BatteryLevel = ContractGuard.Range(batteryLevel, 0, 1, nameof(batteryLevel));
        Status = status;
    }

    /// <summary>Gets the stable agent ID.</summary>
    public int AgentId { get; }
    /// <summary>Gets the navigation class.</summary>
    public AgentRole Role { get; }
    /// <summary>Gets the position in world meters.</summary>
    public Vector2D CurrentPosition { get; }
    /// <summary>Gets planar velocity in meters per second.</summary>
    public Vector2D CurrentVelocity { get; }
    /// <summary>Gets remaining battery as a fraction in [0, 1].</summary>
    public float BatteryLevel { get; }
    /// <summary>Gets the operational state.</summary>
    public AgentStatus Status { get; }
}
