using System;
using System.Diagnostics;
using System.IO;
using OpenCvSharp;

namespace OmniTwin.Perception.Vision;

/// <summary>An owned JPEG image and its capture metadata; no native camera buffers escape.</summary>
public sealed class CapturedFrame
{
    private readonly byte[] _jpeg;
    internal CapturedFrame(long sequence, DateTimeOffset capturedAt, int width, int height, byte[] ownedJpeg,
        FrameChangeDecision decision, long? captureTimestamp = null)
    {
        SequenceNumber = sequence; CapturedAt = capturedAt; Width = width; Height = height;
        _jpeg = ownedJpeg; Change = decision; CaptureTimestamp = captureTimestamp ?? Stopwatch.GetTimestamp();
    }
    /// <summary>Gets the capture sequence within this session.</summary>
    public long SequenceNumber { get; }
    /// <summary>Gets UTC capture time.</summary>
    public DateTimeOffset CapturedAt { get; }
    /// <summary>Gets source width.</summary>
    public int Width { get; }
    /// <summary>Gets source height.</summary>
    public int Height { get; }
    /// <summary>Gets the frame-change measurement and scheduling decision.</summary>
    public FrameChangeDecision Change { get; }
    /// <summary>Returns an independently owned copy of the encoded JPEG.</summary>
    public byte[] CopyJpeg() => (byte[])_jpeg.Clone();
    internal ReadOnlySpan<byte> Jpeg => _jpeg;
    internal byte[] EncodedJpeg => _jpeg;
    internal long CaptureTimestamp { get; }

    /// <summary>Validates a JPEG and re-encodes its displayed orientation without EXIF metadata for segmentation.</summary>
    /// <remarks>Native decoding is synchronous; call on a background thread when used from a UI.</remarks>
    public static CapturedFrame FromJpeg(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg.Length > 8_388_608 || jpeg[0] != 255 || jpeg[1] != 216)
            throw new InvalidDataException("Expected a JPEG of at most 8 MiB.");
        var position = 2;
        var width = 0;
        var height = 0;
        while (position + 4 <= jpeg.Length)
        {
            if (jpeg[position++] != 255) throw new InvalidDataException("Invalid JPEG marker.");
            while (position < jpeg.Length && jpeg[position] == 255) position++;
            if (position >= jpeg.Length) break;
            var marker = jpeg[position++];
            if (marker == 217 || marker == 218) break;
            if (marker == 1 || (marker >= 208 && marker <= 215)) continue;
            if (position + 2 > jpeg.Length) break;
            var length = (jpeg[position] << 8) | jpeg[position + 1];
            if (length < 2 || position + length > jpeg.Length) throw new InvalidDataException("Truncated JPEG segment.");
            if (marker >= 192 && marker <= 207 && marker != 196 && marker != 200 && marker != 204)
            {
                if (length < 8) throw new InvalidDataException("Invalid JPEG frame header.");
                height = (jpeg[position + 3] << 8) | jpeg[position + 4];
                width = (jpeg[position + 5] << 8) | jpeg[position + 6];
                break;
            }
            position += length;
        }
        if (width <= 0 || height <= 0 || (long)width * height > 4_194_304)
            throw new InvalidDataException("JPEG dimensions are missing or exceed four megapixels.");
        var owned = jpeg.ToArray();
        using var image = Cv2.ImDecode(owned, ImreadModes.Color);
        if (image.Empty() || (long)image.Width * image.Height > 4_194_304 ||
            !((image.Width == width && image.Height == height) || (image.Width == height && image.Height == width)))
            throw new InvalidDataException("JPEG decoding failed or dimensions disagree.");
        Cv2.ImEncode(".jpg", image, out var normalized, new ImageEncodingParam(ImwriteFlags.JpegQuality, 85));
        if (normalized.Length == 0 || normalized.Length > 8_388_608) throw new InvalidDataException("JPEG normalization failed.");
        return new CapturedFrame(0, DateTimeOffset.UtcNow, image.Width, image.Height, normalized, new FrameChangeDecision(1, true));
    }

    /// <summary>Validates JPEG bytes and attaches an owned source sequence and capture clocks for a frame-source adapter.</summary>
    public static CapturedFrame FromJpeg(ReadOnlySpan<byte> jpeg, long sequenceNumber, DateTimeOffset capturedAt, long captureTimestamp)
    {
        if (sequenceNumber < 0 || captureTimestamp < 0) throw new ArgumentOutOfRangeException(nameof(sequenceNumber));
        var validated = FromJpeg(jpeg);
        return new CapturedFrame(sequenceNumber, capturedAt, validated.Width, validated.Height, validated.CopyJpeg(),
            new FrameChangeDecision(1, true), captureTimestamp);
    }
}
