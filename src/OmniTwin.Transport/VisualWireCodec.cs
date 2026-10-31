using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using OmniTwin.Contracts;
using OmniTwin.Perception.Imaging;
using static OmniTwin.Transport.WorldStreamCodec;

namespace OmniTwin.Transport;

/// <summary>A single fresh camera JPEG and transparent mask PNG, with common dimensions/capture identity and immutable provenance.</summary>
public sealed class AnnotatedCameraPreview
{
    private readonly byte[] _layer;
    /// <summary>Validates the complete image pair and every source-to-display relation before native rendering.</summary>
    public AnnotatedCameraPreview(CameraPreview image, Guid? trackingSessionId, byte[] maskLayer, IReadOnlyList<PipMaskMetadata> masks, long overlayRevision = 0)
    {
        Image = image ?? throw new ArgumentNullException(nameof(image));
        if (overlayRevision < 0 || trackingSessionId == Guid.Empty || masks is null || masks.Count > 128 || (masks.Count > 0 && !trackingSessionId.HasValue)) throw new WorldStreamProtocolException("mask_session");
        try
        {
            var raster = RasterContainer.Inspect(maskLayer, MediaProtocolLimits.PreviewMaximumSide, MediaProtocolLimits.PreviewMaximumBytes);
            if (!raster.IsPng || raster.Width != image.Width || raster.Height != image.Height) throw new InvalidDataException();
        }
        catch (InvalidDataException) { throw new WorldStreamProtocolException("mask_image_dimensions"); }
        var copy = new PipMaskMetadata[masks.Count]; var ids = new HashSet<int>();
        for (var i = 0; i < copy.Length; i++)
        {
            var mask = masks[i];
            if (mask is null || !ids.Add(mask.TrackId) || mask.SamSequenceNumber > image.SequenceNumber || mask.SamCapturedAtUnixMs > image.CapturedAtUnixMs ||
                image.CapturedAtUnixMs - mask.SamCapturedAtUnixMs >= PerceptionTiming.MaximumFrameAgeMilliseconds ||
                (mask.ObservationSource == ObservationSource.SamVerified) != (mask.SamSequenceNumber == image.SequenceNumber && mask.SamCapturedAtUnixMs == image.CapturedAtUnixMs))
                throw new WorldStreamProtocolException("mask_frame_correlation");
            copy[i] = mask;
        }
        TrackingSessionId = trackingSessionId; Masks = Array.AsReadOnly(copy); _layer = (byte[])maskLayer.Clone(); OverlayRevision = overlayRevision;
    }
    /// <summary>Gets the exact paired capture.</summary>
    public CameraPreview Image { get; }
    /// <summary>Gets monotonic mask revision, which never extends image lifetime.</summary>
    public long OverlayRevision { get; }
    /// <summary>Gets stable track identity scope.</summary>
    public Guid? TrackingSessionId { get; }
    /// <summary>Gets immutable mask provenance.</summary>
    public IReadOnlyList<PipMaskMetadata> Masks { get; }
    /// <summary>Returns an independent transparent layer.</summary>
    public byte[] CopyMaskLayer() => (byte[])_layer.Clone();
}

/// <summary>Bounded scenery status, separate from the texture download.</summary>
public sealed class SceneryGenerationState
{
    internal SceneryGenerationState(Guid requestId, string status, string? failureCategory) { RequestId = requestId; Status = status; FailureCategory = failureCategory; }
    /// <summary>Gets idempotent request identity.</summary>
    public Guid RequestId { get; }
    /// <summary>Gets generating, ready or failed.</summary>
    public string Status { get; }
    /// <summary>Gets safe failure text.</summary>
    public string? FailureCategory { get; }
}

/// <summary>Closed JSON schemas for optional visual services, independent of navigation schemas.</summary>
public static class VisualWireCodec
{
    /// <summary>Maximum image-pair response, including base64 expansion and metadata.</summary>
    public const int MaximumBytes = 524288;
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    /// <summary>Encodes bounded local visual-service JSON.</summary>
    public static byte[] Encode<T>(T value)
    { var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options); if (bytes.Length > MaximumBytes) throw new WorldStreamProtocolException("visual_body_size"); return bytes; }
    private static T Decode<T>(ReadOnlyMemory<byte> bytes, Func<JsonElement, T> read)
    {
        if (bytes.Length == 0 || bytes.Length > MaximumBytes) throw new WorldStreamProtocolException("visual_body_size");
        try { using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 }); return read(json.RootElement); }
        catch (WorldStreamProtocolException) { throw; }
        catch (Exception error) when (error is JsonException || error is ArgumentException || error is InvalidOperationException || error is FormatException || error is OverflowException)
        { throw new WorldStreamProtocolException("invalid_visual_reply"); }
    }
    /// <summary>Decodes an atomically paired image and mask layer, including provenance and source age relationships.</summary>
    public static AnnotatedCameraPreview DecodePreview(ReadOnlyMemory<byte> bytes) => Decode(bytes, root =>
    {
        Keys(root, "schemaVersion", "sessionId", "calibrationId", "sequenceNumber", "capturedAtUnixMs", "width", "height", "jpeg", "trackingSessionId", "maskLayer", "masks", "overlayRevision"); Version(root);
        var image = new CameraPreview(Id(root, "sessionId"), Id(root, "calibrationId"), Long(root, "sequenceNumber"), Long(root, "capturedAtUnixMs"), Int(root, "width"), Int(root, "height"), Convert.FromBase64String(Text(root, "jpeg")));
        var list = new List<PipMaskMetadata>(); var masks = root.GetProperty("masks");
        if (masks.ValueKind != JsonValueKind.Array || masks.GetArrayLength() > 128) throw new WorldStreamProtocolException("mask_count");
        foreach (var mask in masks.EnumerateArray())
        {
            Keys(mask, "trackId", "confidence", "trackingQuality", "isHazardZone", "observationSource", "samSequenceNumber", "samCapturedAtUnixMs");
            list.Add(new PipMaskMetadata(Int(mask, "trackId"), Scalar(mask, "confidence"), Scalar(mask, "trackingQuality"), Boolean(mask, "isHazardZone"), (ObservationSource)Int(mask, "observationSource"), Long(mask, "samSequenceNumber"), Long(mask, "samCapturedAtUnixMs")));
        }
        return new AnnotatedCameraPreview(image, root.GetProperty("trackingSessionId").ValueKind == JsonValueKind.Null ? (Guid?)null : Id(root, "trackingSessionId"), Convert.FromBase64String(Text(root, "maskLayer")), list, Long(root, "overlayRevision"));
    });
    /// <summary>Decodes session-scoped remembered classifications.</summary>
    public static SemanticSceneState DecodeSemantics(ReadOnlyMemory<byte> bytes) => Decode(bytes, root =>
    {
        Keys(root, "schemaVersion", "sessionId", "objects"); Version(root); var list = new List<SemanticObjectState>();
        var objects = root.GetProperty("objects"); if (objects.ValueKind != JsonValueKind.Array || objects.GetArrayLength() > 128) throw new WorldStreamProtocolException("semantic_count");
        foreach (var item in objects.EnumerateArray())
        {
            Keys(item, "trackId", "status", "classification", "operatorOverride", "failureCategory");
            list.Add(new SemanticObjectState(Int(item, "trackId"), Text(item, "status"), ReadTag(item.GetProperty("classification")), Boolean(item, "operatorOverride"), NullableText(item, "failureCategory")));
        }
        return new SemanticSceneState(root.GetProperty("sessionId").ValueKind == JsonValueKind.Null ? (Guid?)null : Id(root, "sessionId"), list);
    });
    private static ObjectSemantics? ReadTag(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        Keys(value, "label", "role", "hazardLevel", "confidence", "isHazardZone");
        var tag = new ObjectSemantics(Text(value, "label"), Text(value, "role"), Text(value, "hazardLevel"), Scalar(value, "confidence"));
        if (Boolean(value, "isHazardZone") != tag.IsHazardZone) throw new WorldStreamProtocolException("semantic_hazard_mismatch"); return tag;
    }
    /// <summary>Decodes an explicit operator edit; null classification clears the manual override.</summary>
    public static (Guid SessionId, int TrackId, ObjectSemantics? Classification) DecodeOverride(ReadOnlyMemory<byte> bytes) => Decode(bytes, root =>
    { Keys(root, "sessionId", "trackId", "classification"); return (Id(root, "sessionId"), Int(root, "trackId"), ReadTag(root.GetProperty("classification"))); });
    /// <summary>Decodes an explicit texture generation request.</summary>
    public static (Guid RequestId, string Prompt) DecodeSceneryRequest(ReadOnlyMemory<byte> bytes) => Decode(bytes, root =>
    { Keys(root, "requestId", "prompt"); var prompt = Text(root, "prompt"); if (prompt.Length > 1000) throw new WorldStreamProtocolException("scenery_prompt_size"); return (Id(root, "requestId"), prompt); });
    /// <summary>Decodes safe asynchronous generation state.</summary>
    public static SceneryGenerationState DecodeSceneryState(ReadOnlyMemory<byte> bytes) => Decode(bytes, root =>
    {
        Keys(root, "requestId", "status", "failureCategory", "modelId");
        if (Text(root, "modelId") != "muse-image-1.0") throw new WorldStreamProtocolException("scenery_model");
        var status = Text(root, "status"); if (status != "generating" && status != "ready" && status != "failed") throw new WorldStreamProtocolException("scenery_status");
        var failure = NullableText(root, "failureCategory"); if (failure != null) foreach (var c in failure) if (!(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '_') throw new WorldStreamProtocolException("scenery_failure");
        return new SceneryGenerationState(Id(root, "requestId"), status, failure);
    });
    private static void Version(JsonElement value) { if (Int(value, "schemaVersion") != 1) throw new WorldStreamProtocolException("visual_schema"); }
}
