using OmniTwin.Contracts;

namespace OmniTwin.Perception.Vision;

/// <summary>A verified mask translated to one exact camera sequence by measured image motion, never by wall-clock prediction.</summary>
public sealed class MaskOverlaySample
{
    internal MaskOverlaySample(int id, SamDetection mask, double shiftU, double shiftV, long samSequence, long samUnix, long imageSequence, float quality)
    { TrackId = id; Mask = mask; ShiftU = shiftU; ShiftV = shiftV; SamSequenceNumber = samSequence; SamCapturedAtUnixMs = samUnix; ImageSequenceNumber = imageSequence; Quality = quality; }
    /// <summary>Gets stable tracker identity.</summary>
    public int TrackId { get; }
    /// <summary>Gets original immutable SAM pixels.</summary>
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
    public ObservationSource ObservationSource => ImageSequenceNumber == SamSequenceNumber ? ObservationSource.SamVerified : ObservationSource.LocalMotionEstimate;
    /// <summary>Gets local alignment quality, independent of detector confidence.</summary>
    public float Quality { get; }
}
