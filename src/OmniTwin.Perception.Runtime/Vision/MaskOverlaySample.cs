using System;
using OmniTwin.Contracts;

namespace OmniTwin.Perception.Vision;

/// <summary>A local or verified mask aligned to one exact image sequence; estimates use measured motion, never wall-clock prediction.</summary>
public sealed class MaskOverlaySample
{
    private readonly ObservationSource _observationSource;
    internal MaskOverlaySample(int id, SamDetection mask, double shiftU, double shiftV, long samSequence, long samUnix, long imageSequence, float quality,
        ObservationSource? observationSource = null)
    {
        TrackId = id; Mask = mask; ShiftU = shiftU; ShiftV = shiftV; SamSequenceNumber = samSequence; SamCapturedAtUnixMs = samUnix;
        ImageSequenceNumber = imageSequence; Quality = quality;
        _observationSource = observationSource ?? (imageSequence == samSequence ? ObservationSource.SamVerified : ObservationSource.LocalMotionEstimate);
    }

    /// <summary>Creates a bounded exact-frame mask from local contours in an explicitly synthetic replay.</summary>
    /// <remarks>The generated raster is copied and normalized to binary bits. This factory never represents SAM output.</remarks>
    public static MaskOverlaySample FromLocalImageSegmentation(int trackId, int left, int top, int right, int bottom, float confidence,
        int maskWidth, int maskHeight, byte[] foregroundMask, long frameSequenceNumber, long capturedAtUnixMs)
    {
        if (trackId < 1 || left < 0 || top < 0 || right < left || bottom < top || maskWidth != right - left + 1 || maskHeight != bottom - top + 1 ||
            maskWidth < 1 || maskHeight < 1 || maskWidth > 640 || maskHeight > 480 || float.IsNaN(confidence) || float.IsInfinity(confidence) || confidence < 0 || confidence > 1 ||
            foregroundMask == null || foregroundMask.Length != checked(maskWidth * maskHeight) || frameSequenceNumber < 0 || capturedAtUnixMs < 0)
            throw new ArgumentException("A local replay mask must have bounded matching image coordinates, confidence, and capture identity.");

        var ownedMask = new byte[foregroundMask.Length];
        var hasForeground = false;
        for (var i = 0; i < ownedMask.Length; i++)
        {
            ownedMask[i] = foregroundMask[i] == 0 ? (byte)0 : (byte)1;
            hasForeground |= ownedMask[i] != 0;
        }
        if (!hasForeground) throw new ArgumentException("A local replay mask must contain foreground pixels.", nameof(foregroundMask));
        var detection = new SamDetection(trackId, new PixelBox(left, top, right, bottom), confidence, maskWidth, maskHeight, ownedMask);
        return new MaskOverlaySample(trackId, detection, 0, 0, frameSequenceNumber, capturedAtUnixMs, frameSequenceNumber, 1,
            ObservationSource.LocalImageSegmentation);
    }
    /// <summary>Gets stable tracker identity.</summary>
    public int TrackId { get; }
    /// <summary>Gets the immutable source mask pixels, either model-verified or produced by explicitly synthetic local CV.</summary>
    public SamDetection Mask { get; }
    /// <summary>Gets measured horizontal displacement in source pixels.</summary>
    public double ShiftU { get; }
    /// <summary>Gets measured vertical displacement in source pixels.</summary>
    public double ShiftV { get; }
    /// <summary>Gets the SAM capture sequence.</summary>
    public long SamSequenceNumber { get; }
    /// <summary>Gets original semantic verification capture time.</summary>
    public long SamCapturedAtUnixMs { get; }
    /// <summary>Gets the exact image sequence to which the displacement applies.</summary>
    public long ImageSequenceNumber { get; }
    /// <summary>Gets measurement origin: only an exact original frame is SamVerified.</summary>
    public ObservationSource ObservationSource => _observationSource;
    /// <summary>Gets local alignment quality, independent of detector confidence.</summary>
    public float Quality { get; }
}
