using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OmniTwin.Backend.Calibration;
using OmniTwin.Contracts;
using OmniTwin.Perception.Audio;
using OmniTwin.Transport;

namespace OmniTwin.Backend.Media;

/// <summary>Bounded local media endpoints; custom headers and same-origin checks prevent cross-site capture/upload.</summary>
public static class MediaEndpoints
{
    /// <summary>Maps short-recording transcription and fresh camera preview without starting hardware.</summary>
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/camera/preview", (HttpContext context, CameraPreviewStore store) =>
        {
            if (!Allowed(context)) return Error("origin_denied", 403);
            var frame = store.Read(); if (frame is null) return Error(store.FailureCategory ?? "camera_preview_unavailable", 503);
            context.Response.Headers["X-OmniTwin-Camera-Session"] = frame.SessionId.ToString("D");
            context.Response.Headers["X-OmniTwin-Calibration"] = frame.CalibrationId.ToString("D");
            context.Response.Headers["X-OmniTwin-Frame"] = frame.SequenceNumber.ToString(CultureInfo.InvariantCulture);
            context.Response.Headers["X-OmniTwin-Captured"] = frame.CapturedAtUnixMs.ToString(CultureInfo.InvariantCulture);
            context.Response.Headers["X-OmniTwin-Width"] = frame.Width.ToString(CultureInfo.InvariantCulture);
            context.Response.Headers["X-OmniTwin-Height"] = frame.Height.ToString(CultureInfo.InvariantCulture);
            return Results.Bytes(frame.CopyJpeg(), "image/jpeg");
        });
        app.MapPost("/api/voice/transcribe", (Func<HttpContext, VoiceTranscriptionService, Task<IResult>>)Transcribe);
    }
    internal static bool Allowed(HttpContext context) => context.Request.Headers["X-OmniTwin-Media"] == "1" && CalibrationEndpoints.SameOrigin(context.Request);
    private static async Task<IResult> Transcribe(HttpContext context, VoiceTranscriptionService service)
    {
        if (!Allowed(context)) return Error("origin_denied", 403);
        if (context.Request.ContentType?.Split(';')[0].Trim() != "audio/wav") return Error("wav_required", 415);
        if (!Guid.TryParseExact(context.Request.Headers["X-OmniTwin-Voice-Request"], "D", out var id) || id == Guid.Empty) return Error("invalid_voice_request_id", 400);
        if (context.Request.ContentLength > MediaProtocolLimits.AudioMaximumBytes) return Error("voice_body_too_large", 413);
        using var upload = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted); upload.CancelAfter(TimeSpan.FromSeconds(2));
        var buffer = new byte[MediaProtocolLimits.AudioMaximumBytes + 1]; var size = 0;
        try
        {
            while (size < buffer.Length) { var count = await context.Request.Body.ReadAsync(buffer.AsMemory(size), upload.Token).ConfigureAwait(false); if (count == 0) break; size += count; }
            if (size > MediaProtocolLimits.AudioMaximumBytes) return Error("voice_body_too_large", 413);
            PcmWaveCodec.Validate(buffer.AsSpan(0, size));
            var result = await service.TranscribeAsync(id, buffer.AsMemory(0, size), context.RequestAborted).ConfigureAwait(false);
            return Results.Bytes(MediaWireCodec.EncodeTranscription(result), "application/json");
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException) { return Error("invalid_voice_audio", 400); }
        catch (InvalidOperationException) { return Error("conflicting_voice_request", 409); }
        catch (VoiceApiException error)
        {
            var code = error.Category switch { "auth_missing_or_invalid_401" => 401, "permission_denied_403" => 403, "voice_busy" or "rate_limited_429" => 429, "upstream_timeout" => 504, _ => 502 };
            if (code == 429) context.Response.Headers.RetryAfter = "1";
            return Error(error.Category, code);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested) { return Error("voice_body_timeout", 408); }
        finally { Array.Clear(buffer); }
    }
    internal static IResult Error(string category, int status) => Results.Json(new { error = category }, statusCode: status);
}
