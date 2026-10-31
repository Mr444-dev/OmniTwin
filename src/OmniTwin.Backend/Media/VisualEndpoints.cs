using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OmniTwin.Transport;

namespace OmniTwin.Backend.Media;

/// <summary>Local guarded visual-service endpoints; model operations are queued and never run on a preview/status request.</summary>
public static class VisualEndpoints
{
    /// <summary>Maps paired mask preview, semantic memory/operator edits, and explicit scenery generation.</summary>
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/camera/annotated", (HttpContext context, CameraPreviewStore store) =>
        {
            if (!MediaEndpoints.Allowed(context)) return MediaEndpoints.Error("origin_denied", 403);
            var frame = store.Read(); if (frame is null) return MediaEndpoints.Error(store.FailureCategory ?? "camera_preview_unavailable", 503);
            return Results.Bytes(VisualWireCodec.Encode(new { schemaVersion = 1, frame.SessionId, frame.CalibrationId, frame.SequenceNumber,
                frame.CapturedAtUnixMs, frame.Width, frame.Height, jpeg = frame.CopyJpeg(), frame.TrackingSessionId, maskLayer = frame.CopyMaskLayer(), frame.Masks, frame.OverlayRevision }), "application/json");
        });
        app.MapGet("/api/semantics/status", (HttpContext context, ObjectSemanticService service) =>
            !MediaEndpoints.Allowed(context) ? MediaEndpoints.Error("origin_denied", 403) : Results.Bytes(VisualWireCodec.Encode(service.Read()), "application/json"));
        app.MapPost("/api/semantics/override", async (HttpContext context, ObjectSemanticService service) =>
        {
            if (!MediaEndpoints.Allowed(context)) return MediaEndpoints.Error("origin_denied", 403);
            try
            {
                var edit = VisualWireCodec.DecodeOverride(await Body(context).ConfigureAwait(false));
                return !service.Override(edit.SessionId, edit.TrackId, edit.Classification) ? MediaEndpoints.Error("semantic_session_or_track_changed", 409) : Results.Bytes(VisualWireCodec.Encode(service.Read()), "application/json");
            }
            catch (Exception error) when (error is WorldStreamProtocolException or ArgumentException or InvalidDataException) { return MediaEndpoints.Error("invalid_semantic_edit", 400); }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested) { return MediaEndpoints.Error("visual_body_timeout", 408); }
        });
        app.MapPost("/api/scenery/generate", async (HttpContext context, SceneryService service) =>
        {
            if (!MediaEndpoints.Allowed(context)) return MediaEndpoints.Error("origin_denied", 403);
            try
            {
                var request = VisualWireCodec.DecodeSceneryRequest(await Body(context).ConfigureAwait(false));
                return Results.Json(service.Submit(request.RequestId, request.Prompt), statusCode: 202);
            }
            catch (Exception error) when (error is WorldStreamProtocolException or ArgumentException or InvalidDataException) { return MediaEndpoints.Error("invalid_scenery_request", 400); }
            catch (InvalidOperationException) { return MediaEndpoints.Error("conflicting_scenery_request", 409); }
            catch (OmniTwin.Perception.Models.ModelApiException) { return MediaEndpoints.Error("scenery_busy", 429); }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested) { return MediaEndpoints.Error("visual_body_timeout", 408); }
        });
        app.MapGet("/api/scenery/status/{id:guid}", (Guid id, HttpContext context, SceneryService service) =>
        {
            if (!MediaEndpoints.Allowed(context)) return MediaEndpoints.Error("origin_denied", 403);
            var state = service.Read(id); return state is null ? MediaEndpoints.Error("scenery_not_found", 404) : Results.Json(state);
        });
        app.MapGet("/api/scenery/image/{id:guid}", (Guid id, HttpContext context, SceneryService service) =>
        {
            if (!MediaEndpoints.Allowed(context)) return MediaEndpoints.Error("origin_denied", 403);
            var image = service.ReadImage(id); return image is null ? MediaEndpoints.Error("scenery_image_unavailable", 404) : Results.Bytes(image, "image/jpeg");
        });
    }
    private static async Task<byte[]> Body(HttpContext context)
    {
        if (context.Request.ContentType?.Split(';')[0].Trim() != "application/json" || context.Request.ContentLength > 8192) throw new InvalidDataException();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted); stop.CancelAfter(TimeSpan.FromSeconds(2));
        using var body = new MemoryStream(); var buffer = new byte[2048];
        while (true) { var count = await context.Request.Body.ReadAsync(buffer, stop.Token).ConfigureAwait(false); if (count == 0) return body.ToArray(); if (body.Length + count > 8192) throw new InvalidDataException(); await body.WriteAsync(buffer.AsMemory(0, count), stop.Token).ConfigureAwait(false); }
    }
}
