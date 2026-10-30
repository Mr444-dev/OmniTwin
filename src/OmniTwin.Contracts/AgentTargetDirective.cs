namespace OmniTwin.Contracts;

/// <summary>A requested planar destination; collision and workspace checks remain mandatory at ingestion.</summary>
public sealed record AgentTargetDirective
{
    /// <summary>Creates a directive with a finite nonnegative speed in meters per second.</summary>
    public AgentTargetDirective(int agentId, Vector2D targetPosition, float desiredSpeed, string statusTag)
    {
        AgentId = ContractGuard.Id(agentId, nameof(agentId));
        TargetPosition = targetPosition;
        DesiredSpeed = ContractGuard.Range(desiredSpeed, 0, float.MaxValue, nameof(desiredSpeed));
        StatusTag = ContractGuard.Text(statusTag, 128, nameof(statusTag));
    }

    /// <summary>Gets the recipient ID.</summary>
    public int AgentId { get; }
    /// <summary>Gets the destination in world meters.</summary>
    public Vector2D TargetPosition { get; }
    /// <summary>Gets requested speed in meters per second.</summary>
    public float DesiredSpeed { get; }
    /// <summary>Gets a concise operator-facing status.</summary>
    public string StatusTag { get; }
}
