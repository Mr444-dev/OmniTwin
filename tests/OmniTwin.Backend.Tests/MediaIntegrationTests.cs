using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OmniTwin.Backend.Media;
using OmniTwin.Backend.Services;
using OmniTwin.Perception.Audio;
using OmniTwin.Perception.Vision;
using OmniTwin.Transport;
using OpenCvSharp;
using Xunit;

namespace OmniTwin.Backend.Tests;

public sealed class MediaIntegrationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task RealMediaClientTraversesBackendAndProviderAndRetriesAreIdempotent()
    {
        await using var server = await Server.StartAsync(); using var media = new MediaClient(); var id = Guid.NewGuid();
        var wav = PcmWaveCodec.Encode(new float[16000], 16000, 1);
        var draft = await media.TranscribeAsync(server.Http.BaseAddress!, id, wav, Token);
        Assert.Equal("Utwórz kordon ochronny.", draft.Transcript); Assert.Equal(1000, draft.AudioDurationMilliseconds);
        var retry = await media.TranscribeAsync(server.Http.BaseAddress!, id, wav, Token); Assert.Equal(draft.Transcript, retry.Transcript); Assert.Equal(1, server.ProviderCalls);
        wav[^1] = 1;
        Assert.Equal("conflicting_voice_request", (await Assert.ThrowsAsync<MediaTransportException>(() => media.TranscribeAsync(server.Http.BaseAddress!, id, wav, Token))).Category);
        Assert.Equal(1, server.ProviderCalls);
        Assert.False(server.App.Services.GetRequiredService<BackendRuntimeState>().Read().IsObservationCurrent);
    }
    [Fact]
    public async Task NativePreviewIsResizedOwnedAndExpiresWithoutChangingNavigationTrust()
    {
        await using var server = await Server.StartAsync(); using var media = new MediaClient();
        using var image = new Mat(480, 640, MatType.CV_8UC3, new Scalar(20, 70, 110));
        Cv2.ImEncode(".jpg", image, out var jpeg); var captured = CapturedFrame.FromJpeg(jpeg);
        var store = server.App.Services.GetRequiredService<CameraPreviewStore>(); var calibration = Guid.NewGuid(); Assert.True(store.Publish(captured, calibration));
        var preview = await media.ReadPreviewAsync(server.Http.BaseAddress!, Token); Assert.Equal(320, preview.Width); Assert.Equal(240, preview.Height); Assert.Equal(calibration, preview.CalibrationId);
        var copy = preview.CopyJpeg(); copy[0] = 0; Assert.Equal(255, preview.CopyJpeg()[0]);
        Assert.Throws<WorldStreamProtocolException>(() => new CameraPreview(preview.SessionId, calibration, 0, preview.CapturedAtUnixMs, 319, 240, preview.CopyJpeg()));
        var original = preview.CopyJpeg(); var startScan = -1;
        for (var i = 0; i < original.Length - 1; i++) if (original[i] == 255 && original[i + 1] == 218) { startScan = i; break; }
        Assert.True(startScan > 0); var extraFrame = new byte[] { 255, 192, 0, 11, 8, 127, 255, 127, 255, 1, 1, 17, 0 };
        var malicious = original.AsSpan(0, startScan).ToArray().Concat(extraFrame).Concat(original.AsSpan(startScan).ToArray()).ToArray();
        Assert.Throws<WorldStreamProtocolException>(() => new CameraPreview(preview.SessionId, calibration, 0, preview.CapturedAtUnixMs, 320, 240, malicious));
        Assert.False(store.Publish(captured, calibration));
        server.Clock.Advance(800);
        Assert.Equal("camera_preview_unavailable", (await Assert.ThrowsAsync<MediaTransportException>(() => media.ReadPreviewAsync(server.Http.BaseAddress!, Token))).Category);
        Assert.False(server.App.Services.GetRequiredService<BackendRuntimeState>().Read().IsObservationCurrent); store.Clear(); Assert.Null(store.Read());
    }
    [Fact]
    public async Task EndpointsRejectForeignOriginsMissingHeadersAndInvalidAudioBeforeProviderCalls()
    {
        await using var server = await Server.StartAsync();
        using var plain = await server.Http.GetAsync("api/camera/preview", Token); Assert.Equal(HttpStatusCode.Forbidden, plain.StatusCode);
        using var foreign = new HttpRequestMessage(HttpMethod.Get, "api/camera/preview"); foreign.Headers.Add("X-OmniTwin-Media", "1"); foreign.Headers.Add("Origin", "https://foreign.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await server.Http.SendAsync(foreign, Token)).StatusCode);
        using var malformed = await server.Post(Guid.NewGuid(), new byte[100]); Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using var huge = await server.Post(Guid.NewGuid(), new byte[PcmWaveCodec.MaximumBytes + 1]); Assert.Equal(HttpStatusCode.RequestEntityTooLarge, huge.StatusCode);
        Assert.Equal(0, server.ProviderCalls);
    }
    [Fact]
    public async Task IdenticalConcurrentUploadsShareInferenceWhileOtherRecordingsAreRateLimited()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await Server.StartAsync(async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); });
        var id = Guid.NewGuid(); var wav = PcmWaveCodec.Encode(new float[16000], 16000, 1);
        var first = server.Post(id, wav); await entered.Task.WaitAsync(Token);
        var duplicate = server.Post(id, wav);
        using var rejected = await server.Post(Guid.NewGuid(), wav); Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        release.TrySetResult(); using var a = await first; using var b = await duplicate;
        Assert.Equal(HttpStatusCode.OK, a.StatusCode); Assert.Equal(HttpStatusCode.OK, b.StatusCode); Assert.Equal(1, server.ProviderCalls);
    }
    private sealed class Clock : TimeProvider
    {
        private long _offset;
        public override long GetTimestamp() => base.GetTimestamp() + Interlocked.Read(ref _offset);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddSeconds((double)Interlocked.Read(ref _offset) / TimestampFrequency);
        internal void Advance(int ms) => Interlocked.Add(ref _offset, (long)((double)ms * TimestampFrequency / 1000));
    }
    private sealed class Server : IAsyncDisposable
    {
        internal WebApplication App { get; } private readonly WebApplication _provider; private readonly MuseVoiceClient _voice;
        internal HttpClient Http { get; } internal Clock Clock { get; } private int _calls; internal int ProviderCalls => Volatile.Read(ref _calls);
        private Server(WebApplication app, WebApplication provider, MuseVoiceClient voice, Clock clock, Uri origin)
        { App = app; _provider = provider; _voice = voice; Clock = clock; Http = new HttpClient { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(10) }; }
        internal static async Task<Server> StartAsync(Func<CancellationToken, Task>? beforeReply = null)
        {
            var providerBuilder = WebApplication.CreateBuilder(); providerBuilder.Logging.ClearProviders(); providerBuilder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var provider = providerBuilder.Build(); Server? server = null;
            provider.MapPost("/v1/asr/transcribe", async context =>
            {
                Interlocked.Increment(ref server!._calls);
                Assert.Equal("Bearer owned-voice-protocol-key", context.Request.Headers.Authorization);
                var form = await context.Request.ReadFormAsync(context.RequestAborted); using var settings = JsonDocument.Parse(form["request"].ToString());
                Assert.Equal("PUSH_TO_TALK", settings.RootElement.GetProperty("mode").GetString());
                var file = Assert.Single(form.Files); using var memory = new MemoryStream(); await file.CopyToAsync(memory, context.RequestAborted); var duration = PcmWaveCodec.Validate(memory.ToArray());
                if (beforeReply is not null) await beforeReply(context.RequestAborted);
                await context.Response.WriteAsJsonAsync(new { sessionId = context.Request.Query["sessionId"].ToString(), transcript = "Utwórz kordon ochronny.", audioDurationMs = duration, turns = Array.Empty<object>() }, context.RequestAborted);
            });
            await provider.StartAsync(Token); var providerOrigin = provider.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var voice = new MuseVoiceClient(endpoint: new Uri(providerOrigin + "/v1/asr/transcribe"), apiKey: "owned-voice-protocol-key"); var clock = new Clock();
            var app = BackendApplication.Create(Array.Empty<string>(), builder =>
            {
                builder.Logging.ClearProviders(); builder.Configuration["OmniTwin:Port"] = "0"; builder.Configuration["OmniTwin:EnablePerception"] = "false";
                builder.Services.AddSingleton<TimeProvider>(clock); builder.Services.AddSingleton<IMuseVoiceClient>(voice);
            });
            await app.StartAsync(Token); var origin = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            server = new Server(app, provider, voice, clock, new Uri(origin + "/")); return server;
        }
        internal Task<HttpResponseMessage> Post(Guid id, byte[] bytes)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "api/voice/transcribe") { Content = new ByteArrayContent(bytes) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav"); request.Headers.Add("X-OmniTwin-Media", "1"); request.Headers.Add("X-OmniTwin-Voice-Request", id.ToString("D"));
            return Http.SendAsync(request, Token);
        }
        public async ValueTask DisposeAsync() { Http.Dispose(); await App.StopAsync(Token); await App.DisposeAsync(); _voice.Dispose(); await _provider.StopAsync(Token); await _provider.DisposeAsync(); }
    }
}
