using System;
using System.Collections.Generic;
using OmniTwin.Contracts;

namespace OmniTwin.Perception.Tracking;

/// <summary>One projected detection. Its source object ID is diagnostic and never used as persistent identity.</summary>
public sealed class ObstacleObservation
{
    /// <summary>Creates a validated observed footprint and response-local source identity.</summary>
    public ObstacleObservation(int sourceObjectId, string label, Vector2D position, float radiusMeters,
        float confidence, bool isHazardZone)
    {
        var validated = new SegmentedObstacle(sourceObjectId, label, position, radiusMeters, confidence, isHazardZone);
        SourceObjectId = sourceObjectId; Label = validated.Label; Position = position;
        RadiusMeters = radiusMeters; Confidence = confidence; IsHazardZone = isHazardZone;
    }
    /// <summary>Gets the response-local source ID.</summary>
    public int SourceObjectId { get; }
    /// <summary>Gets the semantic label used for association.</summary>
    public string Label { get; }
    /// <summary>Gets the observed world position in meters.</summary>
    public Vector2D Position { get; }
    /// <summary>Gets the projected footprint radius including configured clearance.</summary>
    public float RadiusMeters { get; }
    /// <summary>Gets detector confidence in [0, 1].</summary>
    public float Confidence { get; }
    /// <summary>Gets the operator-configured hazard designation.</summary>
    public bool IsHazardZone { get; }
}

/// <summary>An owned set of projected detections sharing one capture sequence and time.</summary>
public sealed class ObstacleObservationBatch
{
    /// <summary>Creates a batch with at most 128 observations and unique response-local IDs.</summary>
    public ObstacleObservationBatch(long sourceSequenceNumber, long capturedElapsedMs, long capturedAtUnixMs,
        IReadOnlyList<ObstacleObservation> observations)
    {
        if (sourceSequenceNumber < 0 || capturedElapsedMs < 0 || capturedAtUnixMs < 0)
            throw new ArgumentOutOfRangeException(nameof(sourceSequenceNumber), "Sequence and capture times must be nonnegative.");
        if (observations is null) throw new ArgumentNullException(nameof(observations));
        if (observations.Count > 128) throw new ArgumentException("At most 128 observations are accepted.", nameof(observations));
        var copy = new ObstacleObservation[observations.Count];
        var ids = new HashSet<int>();
        for (var i = 0; i < copy.Length; i++)
        {
            var observation = observations[i] ?? throw new ArgumentException("Null observation.", nameof(observations));
            if (!ids.Add(observation.SourceObjectId)) throw new ArgumentException("Duplicate source ID.", nameof(observations));
            copy[i] = observation;
        }
        SourceSequenceNumber = sourceSequenceNumber; CapturedElapsedMs = capturedElapsedMs; CapturedAtUnixMs = capturedAtUnixMs;
        Observations = Array.AsReadOnly(copy);
    }
    /// <summary>Gets the originating camera sequence.</summary>
    public long SourceSequenceNumber { get; }
    /// <summary>Gets monotonic capture milliseconds in the same clock domain as tracker updates.</summary>
    public long CapturedElapsedMs { get; }
    /// <summary>Gets capture wall-clock time for display and transport.</summary>
    public long CapturedAtUnixMs { get; }
    /// <summary>Gets the owned observation list.</summary>
    public IReadOnlyList<ObstacleObservation> Observations { get; }
}
