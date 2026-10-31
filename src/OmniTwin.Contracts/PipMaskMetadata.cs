using System;

namespace OmniTwin.Contracts;

/// <summary>Provenance for a mask raster in an atomically paired camera preview.</summary>
public sealed class PipMaskMetadata
{
    /// <summary>Creates bounded mask metadata; relation to the displayed image is verified by the frame codec.</summary>
    public PipMaskMetadata(int trackId, float confidence, float trackingQuality, bool isHazardZone, ObservationSource observationSource, long samSequenceNumber, long samCapturedAtUnixMs)
    {
        TrackId = ContractGuard.Id(trackId, nameof(trackId)); Confidence = ContractGuard.Range(confidence, 0, 1, nameof(confidence));
        TrackingQuality = ContractGuard.Range(trackingQuality, 0, 1, nameof(trackingQuality));
        if (!Enum.IsDefined(typeof(ObservationSource), observationSource)) throw new ArgumentOutOfRangeException(nameof(observationSource));
        IsHazardZone = isHazardZone; ObservationSource = observationSource;
        SamSequenceNumber = ContractGuard.Nonnegative(samSequenceNumber, nameof(samSequenceNumber)); SamCapturedAtUnixMs = ContractGuard.Nonnegative(samCapturedAtUnixMs, nameof(samCapturedAtUnixMs));
    }
    /// <summary>Gets the stable track ID scoped to the supplied tracking session.</summary>
    public int TrackId { get; }
    /// <summary>Gets source detector confidence; local synthetic CV is never represented as a SAM result.</summary>
    public float Confidence { get; }
    /// <summary>Gets local alignment quality, separately from SAM confidence.</summary>
    public float TrackingQuality { get; }
    /// <summary>Gets effective operator/model hazard coloring.</summary>
    public bool IsHazardZone { get; }
    /// <summary>Gets solid verified versus dashed locally estimated provenance.</summary>
    public ObservationSource ObservationSource { get; }
    /// <summary>Gets the source-mask sequence, or the last SAM sequence for a locally motion-aligned estimate.</summary>
    public long SamSequenceNumber { get; }
    /// <summary>Gets the source-mask capture time; local motion never refreshes it.</summary>
    public long SamCapturedAtUnixMs { get; }
}
