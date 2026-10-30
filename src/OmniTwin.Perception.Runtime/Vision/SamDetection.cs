using System;
using System.Collections.Generic;

namespace OmniTwin.Perception.Vision;

/// <summary>A pixel box with inclusive edges in the submitted image.</summary>
public readonly struct PixelBox
{
    internal PixelBox(int left, int top, int right, int bottom)
    {
        Left = left; Top = top; Right = right; Bottom = bottom;
    }
    /// <summary>Gets the leftmost source pixel.</summary>
    public int Left { get; }
    /// <summary>Gets the topmost source pixel.</summary>
    public int Top { get; }
    /// <summary>Gets the inclusive rightmost source pixel.</summary>
    public int Right { get; }
    /// <summary>Gets the inclusive bottommost source pixel.</summary>
    public int Bottom { get; }
}

/// <summary>One validated image detection. Response-local IDs do not identify objects across independent requests.</summary>
public sealed class SamDetection
{
    private readonly byte[] _mask;
    internal SamDetection(int id, PixelBox box, float confidence, int maskWidth, int maskHeight, byte[] ownedMask)
    {
        ObjectId = id; Box = box; Confidence = confidence; MaskWidth = maskWidth; MaskHeight = maskHeight;
        _mask = ownedMask;
        foreach (var bit in _mask) ForegroundPixelCount += bit;
    }
    /// <summary>Gets the ID scoped to this response.</summary>
    public int ObjectId { get; }
    /// <summary>Gets the inclusive source-pixel bounds.</summary>
    public PixelBox Box { get; }
    /// <summary>Gets the model confidence in [0, 1].</summary>
    public float Confidence { get; }
    /// <summary>Gets the number of mask raster columns.</summary>
    public int MaskWidth { get; }
    /// <summary>Gets the number of mask raster rows.</summary>
    public int MaskHeight { get; }
    /// <summary>Gets foreground area in mask raster pixels.</summary>
    public int ForegroundPixelCount { get; }
    /// <summary>Reads a mask-local pixel without exposing mutable storage.</summary>
    public bool IsForeground(int x, int y)
    {
        if ((uint)x >= (uint)MaskWidth) throw new ArgumentOutOfRangeException(nameof(x));
        if ((uint)y >= (uint)MaskHeight) throw new ArgumentOutOfRangeException(nameof(y));
        return _mask[y * MaskWidth + x] != 0;
    }
}

/// <summary>A complete validated image result retaining capture identity and age information.</summary>
public sealed class SamSegmentationResult
{
    internal SamSegmentationResult(string responseId, CapturedFrame frame, double durationMs, IReadOnlyList<SamDetection> detections)
    {
        ResponseId = responseId; SequenceNumber = frame.SequenceNumber; CapturedAt = frame.CapturedAt;
        Width = frame.Width; Height = frame.Height; DurationMs = durationMs; Detections = detections;
        CaptureTimestamp = frame.CaptureTimestamp;
    }
    /// <summary>Gets the provider response ID.</summary>
    public string ResponseId { get; }
    /// <summary>Gets the originating capture sequence.</summary>
    public long SequenceNumber { get; }
    /// <summary>Gets UTC capture time.</summary>
    public DateTimeOffset CapturedAt { get; }
    /// <summary>Gets source width in pixels.</summary>
    public int Width { get; }
    /// <summary>Gets source height in pixels.</summary>
    public int Height { get; }
    /// <summary>Gets the client operation duration including retries in milliseconds.</summary>
    public double DurationMs { get; }
    /// <summary>Gets an owned, read-only detection list.</summary>
    public IReadOnlyList<SamDetection> Detections { get; }
    internal long CaptureTimestamp { get; }
}
