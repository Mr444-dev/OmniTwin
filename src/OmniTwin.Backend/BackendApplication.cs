using System;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OmniTwin.Backend.Services;
using OmniTwin.Backend.Streaming;
using OmniTwin.Backend.Calibration;
using OmniTwin.Backend.Swarm;
using OmniTwin.Transport;
using OmniTwin.Backend.Media;
using OmniTwin.Perception.Audio;
using OmniTwin.Perception.Models;

namespace OmniTwin.Backend;

/// <summary>Builds the loopback composition host; configuration does not implicitly start camera capture.</summary>
public static class BackendApplication
{
    /// <summary>Creates a host with overridable source/clock dependencies for real transport integration tests.</summary>
    public static WebApplication Create(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);
        var options = BackendOptions.FromConfiguration(builder.Configuration);
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, options.Port));
        builder.Services.TryAddSingleton(options);
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<SnapshotBroker>();
        builder.Services.TryAddSingleton<BackendRuntimeState>();
        builder.Services.TryAddSingleton<SwarmCoordinator>();
        builder.Services.TryAddSingleton<CameraPreviewStore>();
        builder.Services.TryAddSingleton<IMuseVoiceClient, MuseVoiceClient>();
        builder.Services.TryAddSingleton<VoiceTranscriptionService>();
        builder.Services.TryAddSingleton<IObjectSemanticClient, MuseSparkSemanticClient>();
        builder.Services.TryAddSingleton<IMuseImageClient, MuseImageClient>();
        builder.Services.TryAddSingleton<ObjectSemanticService>();
        builder.Services.AddHostedService(services => services.GetRequiredService<ObjectSemanticService>());
        builder.Services.TryAddSingleton<SceneryService>();
        builder.Services.AddHostedService(services => services.GetRequiredService<SceneryService>());
        builder.Services.TryAddSingleton<IWorldPerceptionSource, CameraPerceptionSource>();
        builder.Services.AddHostedService<PerceptionBackgroundService>();
        builder.Services.AddHostedService<BrainMissionService>();
        var app = builder.Build();
        var broker = app.Services.GetRequiredService<SnapshotBroker>();
        app.Lifetime.ApplicationStopping.Register(() => broker.Complete(false));
        app.Use(async (context, next) =>
        {
            var host = context.Request.Host.Host;
            if (!(host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) ||
                context.Connection.RemoteIpAddress is not IPAddress remote || !IPAddress.IsLoopback(remote))
            { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
            context.Response.Headers["Cache-Control"] = "no-store";
            await next(context).ConfigureAwait(false);
        });
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        app.MapGet("/", () => Results.Json(new
        {
            service = "OmniTwin.Backend", subProtocol = WorldStreamCodec.SubProtocol,
            endpoints = new[] { "/health/live", "/health/ready", "/api/status", "/api/world/latest", "/ws/twin", "/calibration", "/api/calibration/validate", "/api/swarm/telemetry", "/api/swarm/mission", "/api/swarm/status", "/api/voice/transcribe", "/api/camera/preview" }
        }));
        app.MapGet("/health/live", () => Results.Json(new { status = "live" }));
        app.MapGet("/api/status", (BackendRuntimeState state) => Results.Json(state.Read()));
        app.MapGet("/health/ready", (BackendRuntimeState state) =>
        {
            var status = state.Read();
            return Results.Json(status, statusCode: status.IsObservationCurrent ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        });
        app.MapGet("/api/world/latest", async (HttpContext context, SnapshotBroker snapshots) =>
        {
            var latest = snapshots.Latest();
            if (latest is null)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(new { error = "no_recent_publication" }, context.RequestAborted).ConfigureAwait(false);
                return;
            }
            context.Response.ContentType = "application/json";
            context.Response.ContentLength = latest.Bytes.Length;
            await context.Response.Body.WriteAsync(latest.Bytes, context.RequestAborted).ConfigureAwait(false);
        });
        app.Map("/ws/twin", TwinStreamEndpoint.HandleAsync);
        CalibrationEndpoints.Map(app);
        SwarmEndpoints.Map(app);
        MediaEndpoints.Map(app);
        VisualEndpoints.Map(app);
        return app;
    }
}
