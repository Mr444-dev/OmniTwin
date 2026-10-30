using System;
using System.Collections.Generic;

namespace OmniTwin.Contracts;

/// <summary>An obstacle retained from an actual observation, with explicit age and decayed tracking confidence.</summary>
public sealed record TrackedObstacleState
{
    /// <summary>Creates tracking metadata. The obstacle's confidence remains the last detector confidence.</summary>
    public TrackedObstacleState(SegmentedObstacle obstacle, long lastObservedUnixMs, long ageMilliseconds,
        float trackingConfidence, bool seenInLatestObservation)
    {
        Obstacle = obstacle ?? throw new ArgumentNullException(nameof(obstacle));
        LastObservedUnixMs = ContractGuard.Nonnegative(lastObservedUnixMs, nameof(lastObservedUnixMs));
        AgeMilliseconds = ContractGuard.Nonnegative(ageMilliseconds, nameof(ageMilliseconds));
        TrackingConfidence = ContractGuard.Range(trackingConfidence, 0, obstacle.Confidence, nameof(trackingConfidence));
        SeenInLatestObservation = seenInLatestObservation;
    }
    /// <summary>Gets the last observed geometry with a session-stable local ID.</summary>
    public SegmentedObstacle Obstacle { get; }
    /// <summary>Gets the original capture time in Unix milliseconds.</summary>
    public long LastObservedUnixMs { get; }
    /// <summary>Gets age measured using a monotonic clock, including inference time.</summary>
    public long AgeMilliseconds { get; }
    /// <summary>Gets the last detector confidence multiplied by the remaining retention fraction.</summary>
    public float TrackingConfidence { get; }
    /// <summary>Gets whether this obstacle matched the latest accepted observation batch.</summary>
    public bool SeenInLatestObservation { get; }
}

/// <summary>An immutable, versioned perception publication with identity scoped to a session and calibration.</summary>
public sealed record ObstacleTrackingSnapshot
{
    /// <summary>Creates a publication with owned collections and unique obstacle IDs.</summary>
    public ObstacleTrackingSnapshot(Guid sessionId, Guid calibrationId, long sequenceNumber,
        long? sourceSequenceNumber, long? sourceAgeMilliseconds, bool isObservationCurrent,
        long timestampUnixMs, IReadOnlyList<TrackedObstacleState> obstacles, int schemaVersion = 1)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("Session ID cannot be empty.", nameof(sessionId));
        if (calibrationId == Guid.Empty) throw new ArgumentException("Calibration ID cannot be empty.", nameof(calibrationId));
        if (schemaVersion != 1) throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        if (sourceSequenceNumber.HasValue) ContractGuard.Nonnegative(sourceSequenceNumber.Value, nameof(sourceSequenceNumber));
        if (sourceAgeMilliseconds.HasValue) ContractGuard.Nonnegative(sourceAgeMilliseconds.Value, nameof(sourceAgeMilliseconds));
        if (sourceSequenceNumber.HasValue != sourceAgeMilliseconds.HasValue || (isObservationCurrent && !sourceSequenceNumber.HasValue))
            throw new ArgumentException("Source sequence and age must be present together; an unobserved scene cannot be current.");
        SessionId = sessionId; CalibrationId = calibrationId; SchemaVersion = schemaVersion;
        SequenceNumber = ContractGuard.Nonnegative(sequenceNumber, nameof(sequenceNumber));
        TimestampUnixMs = ContractGuard.Nonnegative(timestampUnixMs, nameof(timestampUnixMs));
        SourceSequenceNumber = sourceSequenceNumber;
        SourceAgeMilliseconds = sourceAgeMilliseconds; IsObservationCurrent = isObservationCurrent;
        Obstacles = ContractGuard.CopyUnique(obstacles, item => item.Obstacle.Id, nameof(obstacles));
        if (!sourceSequenceNumber.HasValue && Obstacles.Count != 0) throw new ArgumentException("Tracks require an observed source sequence.", nameof(obstacles));
    }
    /// <summary>Gets the supported schema version, one.</summary>
    public int SchemaVersion { get; }
    /// <summary>Gets the tracking session identity; IDs must not be compared across sessions.</summary>
    public Guid SessionId { get; }
    /// <summary>Gets the calibration identity.</summary>
    public Guid CalibrationId { get; }
    /// <summary>Gets the increasing publication sequence within this session.</summary>
    public long SequenceNumber { get; }
    /// <summary>Gets the last accepted camera sequence, or null before any accepted batch.</summary>
    public long? SourceSequenceNumber { get; }
    /// <summary>Gets the monotonic age of the latest accepted capture, including an empty detection batch.</summary>
    public long? SourceAgeMilliseconds { get; }
    /// <summary>Gets whether any accepted capture is younger than the configured retention limit.</summary>
    public bool IsObservationCurrent { get; }
    /// <summary>Gets publication wall-clock time; track aging does not depend on this value.</summary>
    public long TimestampUnixMs { get; }
    /// <summary>Gets the owned, read-only current tracks.</summary>
    public IReadOnlyList<TrackedObstacleState> Obstacles { get; }
}
