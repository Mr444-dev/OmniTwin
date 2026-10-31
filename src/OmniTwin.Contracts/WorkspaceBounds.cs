using System;

namespace OmniTwin.Contracts;

/// <summary>Finite rectangular simulation limits on the X/Z plane.</summary>
public readonly record struct WorkspaceBounds
{
    /// <summary>Creates positive-area bounds in world meters.</summary>
    public WorkspaceBounds(float minimumX, float maximumX, float minimumZ, float maximumZ)
    {
        ContractGuard.Range(minimumX, -10000, 10000, nameof(minimumX));
        ContractGuard.Range(maximumX, -10000, 10000, nameof(maximumX));
        ContractGuard.Range(minimumZ, -10000, 10000, nameof(minimumZ));
        ContractGuard.Range(maximumZ, -10000, 10000, nameof(maximumZ));
        if (maximumX - minimumX < 1 || maximumZ - minimumZ < 1) throw new ArgumentException("Workspace dimensions must be at least one meter.");
        MinimumX = minimumX; MaximumX = maximumX; MinimumZ = minimumZ; MaximumZ = maximumZ;
    }
    /// <summary>Gets the left extent.</summary>
    public float MinimumX { get; }
    /// <summary>Gets the right extent.</summary>
    public float MaximumX { get; }
    /// <summary>Gets the near extent.</summary>
    public float MinimumZ { get; }
    /// <summary>Gets the far extent.</summary>
    public float MaximumZ { get; }
    /// <summary>Gets the authored TacticalDesk limits.</summary>
    public static WorkspaceBounds TacticalDesk => new(-10, 10, -6.25f, 6.25f);
    /// <summary>Clamps a target inside the perimeter with a positive agent margin.</summary>
    public Vector2D Clamp(Vector2D position, float margin)
    {
        ContractGuard.Positive(margin, nameof(margin));
        if (2 * margin >= MaximumX - MinimumX || 2 * margin >= MaximumZ - MinimumZ) throw new ArgumentOutOfRangeException(nameof(margin));
        return new Vector2D(Math.Max(MinimumX + margin, Math.Min(MaximumX - margin, position.X)),
            Math.Max(MinimumZ + margin, Math.Min(MaximumZ - margin, position.Z)));
    }
    /// <summary>Tests full disc containment with the requested margin.</summary>
    public bool Contains(Vector2D position, float margin) => margin >= 0 && position.X >= MinimumX + margin &&
        position.X <= MaximumX - margin && position.Z >= MinimumZ + margin && position.Z <= MaximumZ - margin;
}
