namespace OmniTwin.Contracts;

/// <summary>An immutable circular obstacle footprint on the calibrated plane.</summary>
public sealed record SegmentedObstacle
{
    /// <summary>Creates a validated obstacle; radius is positive and confidence is in [0, 1].</summary>
    public SegmentedObstacle(int id, string label, Vector2D worldPosition, float radiusMeters,
        float confidence, bool isHazardZone)
    {
        Id = ContractGuard.Id(id, nameof(id));
        Label = ContractGuard.Text(label, 128, nameof(label));
        WorldPosition = worldPosition;
        RadiusMeters = ContractGuard.Positive(radiusMeters, nameof(radiusMeters));
        Confidence = ContractGuard.Range(confidence, 0, 1, nameof(confidence));
        IsHazardZone = isHazardZone;
    }

    /// <summary>Gets the stable tracking ID.</summary>
    public int Id { get; }
    /// <summary>Gets the semantic label.</summary>
    public string Label { get; }
    /// <summary>Gets the footprint center in world meters.</summary>
    public Vector2D WorldPosition { get; }
    /// <summary>Gets the conservative footprint radius in world meters.</summary>
    public float RadiusMeters { get; }
    /// <summary>Gets confidence in [0, 1].</summary>
    public float Confidence { get; }
    /// <summary>Gets whether the footprint denotes a hazard zone.</summary>
    public bool IsHazardZone { get; }
}
