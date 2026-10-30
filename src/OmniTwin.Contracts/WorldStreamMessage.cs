using System;

namespace OmniTwin.Contracts;

/// <summary>A versioned world-perception message delivered over the omnitwin.v1 WebSocket subprotocol.</summary>
public sealed record WorldStreamMessage
{
    /// <summary>Creates a bounded, internally consistent transport publication.</summary>
    public WorldStreamMessage(Guid streamId, long sequenceNumber, long sentAtUnixMs,
        long retentionMilliseconds, ObstacleTrackingSnapshot world, string? failureCategory,
        int rejectedDetectionCount, int protocolVersion = 1)
    {
        if (streamId == Guid.Empty) throw new ArgumentException("Stream ID cannot be empty.", nameof(streamId));
        if (protocolVersion != 1) throw new ArgumentOutOfRangeException(nameof(protocolVersion));
        if (retentionMilliseconds < 1 || retentionMilliseconds > 60_000) throw new ArgumentOutOfRangeException(nameof(retentionMilliseconds));
        if (rejectedDetectionCount < 0 || rejectedDetectionCount > 128) throw new ArgumentOutOfRangeException(nameof(rejectedDetectionCount));
        if (failureCategory is not null) ContractGuard.Text(failureCategory, 128, nameof(failureCategory));
        World = world ?? throw new ArgumentNullException(nameof(world));
        if (world.Obstacles.Count > 128) throw new ArgumentException("At most 128 tracks can be transmitted.", nameof(world));
        var expectedCurrent = world.SourceAgeMilliseconds.HasValue && world.SourceAgeMilliseconds.Value < retentionMilliseconds;
        if (world.IsObservationCurrent != expectedCurrent) throw new ArgumentException("Source freshness disagrees with retention.", nameof(world));
        foreach (var track in world.Obstacles)
            if (track.AgeMilliseconds >= retentionMilliseconds || !world.SourceAgeMilliseconds.HasValue ||
                track.AgeMilliseconds < world.SourceAgeMilliseconds.Value)
                throw new ArgumentException("Track age is inconsistent with source age or retention.", nameof(world));
        StreamId = streamId; ProtocolVersion = protocolVersion;
        SequenceNumber = ContractGuard.Nonnegative(sequenceNumber, nameof(sequenceNumber));
        SentAtUnixMs = ContractGuard.Nonnegative(sentAtUnixMs, nameof(sentAtUnixMs));
        RetentionMilliseconds = retentionMilliseconds; FailureCategory = failureCategory;
        RejectedDetectionCount = rejectedDetectionCount;
    }
    /// <summary>Gets the transport protocol version.</summary>
    public int ProtocolVersion { get; }
    /// <summary>Gets the message discriminator.</summary>
    public string MessageType => "world.perception";
    /// <summary>Gets the backend stream identity, renewed after a backend restart.</summary>
    public Guid StreamId { get; }
    /// <summary>Gets the increasing server publication sequence; skipped values are permitted.</summary>
    public long SequenceNumber { get; }
    /// <summary>Gets serialization/publication wall-clock time.</summary>
    public long SentAtUnixMs { get; }
    /// <summary>Gets the monotonic observation retention interval.</summary>
    public long RetentionMilliseconds { get; }
    /// <summary>Gets the immutable tracking snapshot.</summary>
    public ObstacleTrackingSnapshot World { get; }
    /// <summary>Gets the latest sanitized perception failure, or null.</summary>
    public string? FailureCategory { get; }
    /// <summary>Gets the number of rejected detections in the latest projection.</summary>
    public int RejectedDetectionCount { get; }
}
