using System;
using System.Text.Json;
using OmniTwin.Contracts;
using static OmniTwin.Transport.WorldStreamCodec;

namespace OmniTwin.Transport;

/// <summary>Closed local transcription schema, distinct from the upstream provider's response.</summary>
public static class MediaWireCodec
{
    /// <summary>Encodes a validated operator draft.</summary>
    public static byte[] EncodeTranscription(VoiceTranscription value) => JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    /// <summary>Rejects unknown/duplicate fields, noncanonical text, invalid duration and correlation identity.</summary>
    public static VoiceTranscription DecodeTranscription(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length > 32768) throw new WorldStreamProtocolException("invalid_voice_reply");
        try
        {
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 }); var root = json.RootElement;
            Keys(root, "schemaVersion", "requestId", "transcript", "audioDurationMilliseconds"); if (Int(root, "schemaVersion") != 1) throw new WorldStreamProtocolException("invalid_voice_reply");
            var transcript = root.GetProperty("transcript"); if (transcript.ValueKind != JsonValueKind.String) throw new WorldStreamProtocolException("invalid_voice_reply");
            var value = new VoiceTranscription(Id(root, "requestId"), transcript.GetString()!, Int(root, "audioDurationMilliseconds"));
            if (value.Transcript != transcript.GetString()) throw new WorldStreamProtocolException("noncanonical_voice_reply"); return value;
        }
        catch (WorldStreamProtocolException) { throw; }
        catch (Exception error) when (error is JsonException || error is ArgumentException || error is InvalidOperationException || error is OverflowException)
        { throw new WorldStreamProtocolException("invalid_voice_reply"); }
    }
}

/// <summary>Owned bounded preview bytes, checked before any native texture decoder sees them.</summary>
public sealed class CameraPreview
{
    private readonly byte[] _jpeg;
    /// <summary>Validates metadata and the complete bounded JPEG container's dimensions.</summary>
    public CameraPreview(Guid sessionId, Guid calibrationId, long sequenceNumber, long capturedAtUnixMs, int width, int height, byte[] jpeg)
    {
        if (sessionId == Guid.Empty || calibrationId == Guid.Empty || sequenceNumber < 0 || capturedAtUnixMs < 0 || width < 1 || height < 1 ||
            width > MediaProtocolLimits.PreviewMaximumSide || height > MediaProtocolLimits.PreviewMaximumSide || jpeg is null || jpeg.Length < 4 ||
            jpeg.Length > MediaProtocolLimits.PreviewMaximumBytes || jpeg[0] != 255 || jpeg[1] != 216 || jpeg[jpeg.Length - 2] != 255 || jpeg[jpeg.Length - 1] != 217)
            throw new WorldStreamProtocolException("invalid_camera_preview");
        var position = 2; var matched = false; var scan = -1;
        while (position + 4 <= jpeg.Length)
        {
            if (jpeg[position++] != 255) throw new WorldStreamProtocolException("invalid_camera_jpeg");
            while (position < jpeg.Length && jpeg[position] == 255) position++;
            if (position >= jpeg.Length) break; var marker = jpeg[position++]; if (marker == 217) break;
            if (marker == 1 || (marker >= 208 && marker <= 215)) continue;
            if (position + 2 > jpeg.Length) break; var size = (jpeg[position] << 8) | jpeg[position + 1];
            if (size < 2 || position + size > jpeg.Length) throw new WorldStreamProtocolException("invalid_camera_jpeg");
            if (marker == 218) { if (!matched) throw new WorldStreamProtocolException("camera_preview_dimensions"); scan = position + size; break; }
            if (marker >= 192 && marker <= 207 && marker != 196 && marker != 200 && marker != 204)
            {
                if (matched || marker != 192 || size < 8 || jpeg[position + 2] != 8 || ((jpeg[position + 3] << 8) | jpeg[position + 4]) != height || ((jpeg[position + 5] << 8) | jpeg[position + 6]) != width)
                    throw new WorldStreamProtocolException("camera_preview_dimensions");
                matched = true;
            }
            position += size;
        }
        if (!matched || scan < 0) throw new WorldStreamProtocolException("camera_preview_dimensions");
        var ended = false;
        for (var i = scan; i < jpeg.Length - 1; i++)
        {
            if (jpeg[i] != 255) continue;
            while (i + 1 < jpeg.Length && jpeg[i + 1] == 255) i++;
            if (++i >= jpeg.Length) break;
            var marker = jpeg[i]; if (marker == 0 || (marker >= 208 && marker <= 215)) continue;
            if (marker == 217 && i == jpeg.Length - 1) { ended = true; break; }
            throw new WorldStreamProtocolException("unexpected_camera_scan_marker");
        }
        if (!ended) throw new WorldStreamProtocolException("invalid_camera_jpeg");
        SessionId = sessionId; CalibrationId = calibrationId; SequenceNumber = sequenceNumber; CapturedAtUnixMs = capturedAtUnixMs; Width = width; Height = height; _jpeg = (byte[])jpeg.Clone();
    }
    /// <summary>Gets camera-source identity, rotated when capture ends.</summary>
    public Guid SessionId { get; }
    /// <summary>Gets capture calibration.</summary>
    public Guid CalibrationId { get; }
    /// <summary>Gets source camera sequence.</summary>
    public long SequenceNumber { get; }
    /// <summary>Gets actual capture time.</summary>
    public long CapturedAtUnixMs { get; }
    /// <summary>Gets JPEG raster width.</summary>
    public int Width { get; }
    /// <summary>Gets JPEG raster height.</summary>
    public int Height { get; }
    /// <summary>Returns independent bytes for one bounded texture update.</summary>
    public byte[] CopyJpeg() => (byte[])_jpeg.Clone();
}
