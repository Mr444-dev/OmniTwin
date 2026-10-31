using System;
using System.Collections.Generic;
using OmniTwin.Perception.Vision;
using OpenCvSharp;

namespace OmniTwin.Perception.Tracking;

/// <summary>Response-local masks associated with stable IDs after an accepted tracker update.</summary>
public sealed class VerifiedMaskBatch
{
    internal VerifiedMaskBatch(Guid session, SamSegmentationResult result, Dictionary<int, SamDetection> masks)
    { SessionId = session; Result = result; Masks = new System.Collections.ObjectModel.ReadOnlyDictionary<int, SamDetection>(masks); }
    /// <summary>Gets tracking session identity.</summary>
    public Guid SessionId { get; }
    /// <summary>Gets the actual source result.</summary>
    public SamSegmentationResult Result { get; }
    /// <summary>Gets the owned originating capture for exact, sufficiently fresh preview corrections.</summary>
    public CapturedFrame SourceFrame => Result.SourceFrame;
    /// <summary>Gets original-mask overlays for the exact SAM input frame.</summary>
    public IReadOnlyList<MaskOverlaySample> ExactOverlays
    {
        get
        {
            var samples = new List<MaskOverlaySample>();
            foreach (var pair in Masks) samples.Add(new MaskOverlaySample(pair.Key, pair.Value, 0, 0, Result.SequenceNumber, Result.CapturedAt.ToUnixTimeMilliseconds(), Result.SequenceNumber, 1));
            return samples.AsReadOnly();
        }
    }
    /// <summary>Gets owned stable-ID to immutable-mask associations.</summary>
    public IReadOnlyDictionary<int, SamDetection> Masks { get; }
    /// <summary>Creates a foreground-only JPEG crop from the exact SAM input, outside capture/publication threads.</summary>
    public byte[] Crop(int trackId)
    {
        if (!Masks.TryGetValue(trackId, out var detection)) throw new ArgumentException("Track not in this verified result.");
        using var source = Cv2.ImDecode(Result.SourceFrame.CopyJpeg(), ImreadModes.Color);
        var box = detection.Box; var rectangle = new Rect(box.Left, box.Top, box.Right - box.Left + 1, box.Bottom - box.Top + 1);
        using var roi = new Mat(source, rectangle); using var foreground = new Mat(roi.Size(), MatType.CV_8UC1, Scalar.Black);
        var width = roi.Width; var height = roi.Height;
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
            if (detection.IsForeground(Math.Min(detection.MaskWidth - 1, x * detection.MaskWidth / width), Math.Min(detection.MaskHeight - 1, y * detection.MaskHeight / height))) foreground.Set(y, x, (byte)255);
        using var isolated = new Mat(roi.Size(), MatType.CV_8UC3, Scalar.Black); roi.CopyTo(isolated, foreground);
        var scale = Math.Min(1, 512.0 / Math.Max(roi.Width, roi.Height)); using var small = new Mat();
        Cv2.Resize(isolated, small, new Size(Math.Max(1, (int)Math.Round(roi.Width * scale)), Math.Max(1, (int)Math.Round(roi.Height * scale))), interpolation: InterpolationFlags.Area);
        Cv2.ImEncode(".jpg", small, out var bytes, new ImageEncodingParam(ImwriteFlags.JpegQuality, 80));
        if (bytes.Length > 262144) throw new InvalidOperationException("Semantic ROI exceeds bounds."); return bytes;
    }
}
