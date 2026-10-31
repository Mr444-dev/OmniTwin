using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OmniTwin.Backend.Calibration;
using OmniTwin.Transport;

namespace OmniTwin.Backend.Swarm;

/// <summary>Loopback JSON control endpoints; all bodies are bounded, same-origin checked, and validated before use.</summary>
public static class SwarmEndpoints
{
    /// <summary>Maps measured telemetry/control, explicit mission submission, and diagnostic state.</summary>
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/swarm/telemetry", (Func<HttpContext, SwarmCoordinator, Task<IResult>>)TelemetryAsync);
        app.MapPost("/api/swarm/mission", (Func<HttpContext, SwarmCoordinator, Task<IResult>>)MissionAsync);
        app.MapGet("/api/swarm/status", (SwarmCoordinator coordinator) =>
        { var state = coordinator.Read(); return Results.Json(new { telemetry = state.Telemetry, control = state.Control }); });
    }
    private static async Task<IResult> TelemetryAsync(HttpContext context, SwarmCoordinator coordinator)
    {
        var (bytes, error) = await BodyAsync(context).ConfigureAwait(false); if (error is not null) return error;
        try
        {
            var telemetry = SwarmWireCodec.DecodeTelemetry(bytes);
            var control = await coordinator.EvaluateAsync(telemetry, context.RequestAborted).ConfigureAwait(false);
            return Results.Bytes(SwarmWireCodec.EncodeControl(control), "application/json");
        }
        catch (WorldStreamProtocolException) { return Error("invalid_telemetry", 400); }
        catch (ArgumentException) { return Error("invalid_or_stale_telemetry", 400); }
        catch (InvalidOperationException) { return Error("telemetry_order_or_session", 409); }
        catch (IOException) { return Error("workspace_configuration_io", 503); }
    }
    private static async Task<IResult> MissionAsync(HttpContext context, SwarmCoordinator coordinator)
    {
        var (bytes, error) = await BodyAsync(context).ConfigureAwait(false); if (error is not null) return error;
        try
        {
            var mission = SwarmWireCodec.DecodeMission(bytes);
            var age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - mission.Command.IssuedAtUnixMs;
            if (age < -500 || age > 5000) return Error("mission_stale_or_future", 400);
            await coordinator.SubmitMissionAsync(mission, context.RequestAborted).ConfigureAwait(false);
            return Results.Json(new { accepted = true, commandId = mission.Command.CommandId }, statusCode: 202);
        }
        catch (WorldStreamProtocolException) { return Error("invalid_mission", 400); }
        catch (ArgumentException) { return Error("conflicting_mission", 409); }
    }
    private static async Task<(byte[] Bytes, IResult? Error)> BodyAsync(HttpContext context)
    {
        if (context.Request.Headers["X-OmniTwin-Swarm"] != "1" || !CalibrationEndpoints.SameOrigin(context.Request)) return (Array.Empty<byte>(), Error("origin_denied", 403));
        if (!context.Request.HasJsonContentType()) return (Array.Empty<byte>(), Error("json_required", 415));
        if (context.Request.ContentLength > SwarmWireCodec.MaximumBytes) return (Array.Empty<byte>(), Error("body_too_large", 413));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted); deadline.CancelAfter(TimeSpan.FromSeconds(2));
        var buffer = new byte[SwarmWireCodec.MaximumBytes + 1]; var length = 0;
        try
        {
            while (length < buffer.Length)
            { var count = await context.Request.Body.ReadAsync(buffer.AsMemory(length), deadline.Token).ConfigureAwait(false); if (count == 0) break; length += count; }
            return length > SwarmWireCodec.MaximumBytes ? (Array.Empty<byte>(), Error("body_too_large", 413)) : (buffer.AsMemory(0, length).ToArray(), null);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        { return (Array.Empty<byte>(), Error("body_timeout", 408)); }
    }
    private static IResult Error(string category, int status) => Results.Json(new { error = category }, statusCode: status);
}
