using System;

namespace OmniTwin.Contracts;

/// <summary>Summary of an actual local scan; null minimum means every beam missed within the configured range.</summary>
public sealed record LidarTelemetry
{
    /// <summary>Validates scan cardinality, finite distances and monotonic sample age.</summary>
    public LidarTelemetry(int rayCount, int hitCount, float rangeMeters, float? minimumDistanceMeters, int ageMilliseconds)
    {
        if (rayCount < 8 || rayCount > 128 || hitCount < 0 || hitCount > rayCount || ageMilliseconds < 0 || ageMilliseconds > 60000)
            throw new ArgumentOutOfRangeException(nameof(rayCount));
        RangeMeters = ContractGuard.Range(rangeMeters, 0.5f, 30, nameof(rangeMeters));
        if ((hitCount == 0) != !minimumDistanceMeters.HasValue) throw new ArgumentException("Hit count and minimum distance disagree.");
        if (minimumDistanceMeters.HasValue) ContractGuard.Range(minimumDistanceMeters.Value, 0, rangeMeters, nameof(minimumDistanceMeters));
        RayCount = rayCount; HitCount = hitCount; MinimumDistanceMeters = minimumDistanceMeters; AgeMilliseconds = ageMilliseconds;
    }
    /// <summary>Gets beams actually cast.</summary>
    public int RayCount { get; }
    /// <summary>Gets beams that encountered a relevant collider.</summary>
    public int HitCount { get; }
    /// <summary>Gets the maximum measured range.</summary>
    public float RangeMeters { get; }
    /// <summary>Gets the nearest hit, or null for a clear scan.</summary>
    public float? MinimumDistanceMeters { get; }
    /// <summary>Gets elapsed time since this scan, without refreshing it during serialization.</summary>
    public int AgeMilliseconds { get; }
}
