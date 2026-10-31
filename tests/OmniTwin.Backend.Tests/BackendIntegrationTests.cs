using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OmniTwin.Backend;
using OmniTwin.Backend.Media;
using OmniTwin.Backend.Services;
using OmniTwin.Backend.Streaming;
using OmniTwin.Contracts;
using OmniTwin.Transport;
using Xunit;

namespace OmniTwin.Backend.Tests;

public sealed class BackendIntegrationTests
{
    [Fact]
    public async Task DisabledHostHasLivenessButNoFabricatedWorldOrReadiness()
    {
        await using var server = await Server.StartAsync();
        Assert.Equal(HttpStatusCode.OK, (await server.Http.GetAsync("/health/live", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await server.Http.GetAsync("/health/ready", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await server.Http.GetAsync("/api/world/latest", TestContext.Current.CancellationToken)).StatusCode);
        using var state = JsonDocument.Parse(await server.Http.GetStringAsync("/api/status", TestContext.Current.CancellationToken));
        Assert.Equal("disabled", state.RootElement.GetProperty("state").GetString());
        Assert.False(state.RootElement.GetProperty("hasSnapshot").GetBoolean());
        Assert.Equal(HttpStatusCode.UpgradeRequired, (await server.Http.GetAsync("/ws/twin", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task RealLoopbackWebSocketAndHttpCarryTheSameValidatedPublication()
    {
        var source = new Source();
        await using var server = await Server.StartAsync(source);
        using var socket = await server.ConnectAsync();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reader = new WorldStreamReceiver().ReadAllAsync(socket, stop.Token).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var incoming = reader.MoveNextAsync().AsTask();
        await source.Queue.Writer.WriteAsync(new PerceptionPublication(SnapshotBrokerTests.Snapshot(7), null, 0), stop.Token);
        Assert.True(await incoming);
        var message = reader.Current;
        var latest = WorldStreamCodec.Decode(await server.Http.GetByteArrayAsync("/api/world/latest", TestContext.Current.CancellationToken));
        Assert.Equal(message.StreamId, latest.StreamId);
        Assert.Equal(message.SequenceNumber, latest.SequenceNumber);
        Assert.Equal(7, latest.World.SequenceNumber);
        Assert.Equal(1, Assert.Single(latest.World.Obstacles).Obstacle.Id);
        await UntilAsync(async () => (await server.Http.GetAsync("/health/ready")).StatusCode == HttpStatusCode.OK);
        socket.Abort();
    }

    [Fact]
    public async Task SourceFailureClearsStateAndClosesClientsWithAnError()
    {
        var source = new Source();
        await using var server = await Server.StartAsync(source);
        using var socket = await server.ConnectAsync();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reader = new WorldStreamReceiver().ReadAllAsync(socket, stop.Token).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await source.Queue.Writer.WriteAsync(new PerceptionPublication(SnapshotBrokerTests.Snapshot(0), null, 0), stop.Token);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(HttpStatusCode.OK, (await server.Http.GetAsync("/api/world/latest", TestContext.Current.CancellationToken)).StatusCode);
        source.Queue.Writer.TryComplete(new IOException("fixture source failure"));
        var close = await Assert.ThrowsAsync<WorldStreamConnectionException>(() => reader.MoveNextAsync().AsTask());
        Assert.Equal("server_closed_with_error", close.Category);
        using var state = JsonDocument.Parse(await server.Http.GetStringAsync("/api/status", TestContext.Current.CancellationToken));
        Assert.Equal("faulted", state.RootElement.GetProperty("state").GetString());
        Assert.False(state.RootElement.GetProperty("hasSnapshot").GetBoolean());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await server.Http.GetAsync("/api/world/latest", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task ReadOnlyStreamClosesOnClientApplicationData()
    {
        await using var server = await Server.StartAsync();
        using var socket = await server.ConnectAsync();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.SendAsync(Encoding.UTF8.GetBytes("unexpected command"), WebSocketMessageType.Text, true, stop.Token);
        var close = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[1024]), stop.Token);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, close.CloseStatus);
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ack", stop.Token);
    }

    [Fact]
    public async Task RejectsForeignBrowserOriginsMissingSubprotocolAndSpoofedHosts()
    {
        await using var server = await Server.StartAsync();
        using var foreign = new ClientWebSocket();
        foreign.Options.AddSubProtocol(WorldStreamCodec.SubProtocol);
        foreign.Options.CollectHttpResponseDetails = true;
        foreign.Options.SetRequestHeader("Origin", "https://untrusted.example");
        await Assert.ThrowsAsync<WebSocketException>(() => foreign.ConnectAsync(server.WebSocketUri, CancellationToken.None));
        Assert.Equal(HttpStatusCode.Forbidden, foreign.HttpStatusCode);
        using var missing = new ClientWebSocket();
        missing.Options.CollectHttpResponseDetails = true;
        await Assert.ThrowsAsync<WebSocketException>(() => missing.ConnectAsync(server.WebSocketUri, CancellationToken.None));
        Assert.Equal(HttpStatusCode.BadRequest, missing.HttpStatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        request.Headers.Host = "untrusted.example";
        Assert.Equal(HttpStatusCode.Forbidden, (await server.Http.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode);
        using var allowed = await server.ConnectAsync(origin: server.Http.BaseAddress!.GetLeftPart(UriPartial.Authority));
        Assert.Equal(WorldStreamCodec.SubProtocol, allowed.SubProtocol);
    }

    [Fact]
    public async Task SubscriberLimitIsEnforcedAndDisconnectReleasesItsSlot()
    {
        await using var server = await Server.StartAsync(maximumClients: 1);
        using var first = await server.ConnectAsync();
        using var second = new ClientWebSocket();
        second.Options.AddSubProtocol(WorldStreamCodec.SubProtocol);
        second.Options.CollectHttpResponseDetails = true;
        await Assert.ThrowsAsync<WebSocketException>(() => second.ConnectAsync(server.WebSocketUri, CancellationToken.None));
        Assert.Equal(HttpStatusCode.TooManyRequests, second.HttpStatusCode);
        first.Abort();
        await UntilAsync(() => Task.FromResult(server.App.Services.GetRequiredService<SnapshotBroker>().SubscriberCount == 0));
        using var replacement = await server.ConnectAsync();
        Assert.Equal(WebSocketState.Open, replacement.State);
    }

    [Fact]
    public async Task HostShutdownReleasesAnIdleWebSocketWithoutWaitingForInference()
    {
        await using var server = await Server.StartAsync(new Source());
        using var socket = await server.ConnectAsync();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await server.App.StopAsync(stop.Token);
        Assert.Equal(0, server.App.Services.GetRequiredService<SnapshotBroker>().SubscriberCount);
    }

    [Fact]
    public async Task StreamProbeReceivesRequestedCountAndClosesItsSubscriberCleanly()
    {
        var source = new Source();
        await using var server = await Server.StartAsync(source);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var messages = new ConcurrentQueue<long>();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = StreamProbe.RunAsync(server.WebSocketUri, 2, message =>
        { messages.Enqueue(message.SequenceNumber); first.TrySetResult(); }, stop.Token);
        await UntilAsync(() => Task.FromResult(server.App.Services.GetRequiredService<SnapshotBroker>().SubscriberCount == 1));
        await source.Queue.Writer.WriteAsync(new PerceptionPublication(SnapshotBrokerTests.Snapshot(0), null, 0), stop.Token);
        await first.Task.WaitAsync(stop.Token);
        await source.Queue.Writer.WriteAsync(new PerceptionPublication(SnapshotBrokerTests.Snapshot(1), null, 0), stop.Token);
        await probe.WaitAsync(stop.Token);
        Assert.Equal(new long[] { 0, 1 }, messages.ToArray());
        await UntilAsync(() => Task.FromResult(server.App.Services.GetRequiredService<SnapshotBroker>().SubscriberCount == 0));
    }

    [Fact]
    public async Task StreamProbeClassifiesPermanentHandshakeRejection()
    {
        await using var server = await Server.StartAsync();
        var endpoint = new UriBuilder(server.WebSocketUri) { Path = "/missing-endpoint" }.Uri;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var failure = await Assert.ThrowsAsync<WorldStreamConnectionException>(() =>
            StreamProbe.RunAsync(endpoint, 1, _ => Assert.Fail("Rejected stream cannot emit data."), stop.Token));
        Assert.Equal("connect_rejected_or_unavailable", failure.Category);
    }

    [Fact]
    public async Task DesktopClientConsumesRealServerAndClearsItsWorldOnCancellation()
    {
        var source = new Source();
        await using var server = await Server.StartAsync(source);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var client = new WorldStreamClient();
        var run = client.RunAsync(server.WebSocketUri, stop.Token);
        try
        {
            await UntilAsync(() => Task.FromResult(client.GetView().State.Phase == WorldStreamClientPhase.Connected));
            await source.Queue.Writer.WriteAsync(new PerceptionPublication(SnapshotBrokerTests.Snapshot(0), null, 0), stop.Token);
            await UntilAsync(() => Task.FromResult(client.GetView().State.Message is not null));
            Assert.True(client.GetView().IsObservationCurrent);
            Assert.Equal(1, Assert.Single(client.GetView().State.Message!.World.Obstacles).Obstacle.Id);
        }
        finally { stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); }
        Assert.Null(client.GetView().State.Message);
        await UntilAsync(() => Task.FromResult(server.App.Services.GetRequiredService<SnapshotBroker>().SubscriberCount == 0));
    }

    [Fact]
    public async Task DesktopConnectorClassifiesActualHandshakeStatusAndRetryAdvice()
    {
        await using var server = await Server.StartAsync(configureRoutes: app =>
            app.MapGet("/retry", (Microsoft.AspNetCore.Http.HttpContext context) =>
            {
                context.Response.StatusCode = 503;
                context.Response.Headers["Retry-After"] = "2";
                return Task.CompletedTask;
            }));
        var connector = new ClientWebSocketConnector();
        var retry = new UriBuilder(server.WebSocketUri) { Path = "/retry" }.Uri;
        var busy = await Assert.ThrowsAsync<WorldStreamConnectException>(() => connector.ConnectAsync(retry, TestContext.Current.CancellationToken));
        Assert.True(busy.IsTransient);
        Assert.Equal("handshake_503", busy.Category);
        Assert.Equal(TimeSpan.FromSeconds(2), busy.RetryAfter);
        var missing = new UriBuilder(server.WebSocketUri) { Path = "/missing" }.Uri;
        var denied = await Assert.ThrowsAsync<WorldStreamConnectException>(() => connector.ConnectAsync(missing, TestContext.Current.CancellationToken));
        Assert.False(denied.IsTransient);
        Assert.Equal("handshake_404", denied.Category);
    }

    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await condition()) await Task.Delay(10, deadline.Token);
    }

    [Fact]
    public async Task SyntheticTabletopReplayUsesLocalContoursAndNeverClaimsSamVerification()
    {
        var preview = new CameraPreviewStore(TimeProvider.System);
        var source = new SyntheticTabletopReplaySource(new BackendOptions(retentionMilliseconds: 20_000), preview, TimeProvider.System);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var updates = source.ReadAllAsync(stop.Token).GetAsyncEnumerator(stop.Token);
        Assert.True(await updates.MoveNextAsync());
        var first = updates.Current;
        Assert.Equal(PerceptionSourceMode.SyntheticTabletopReplay, first.SourceMode);
        Assert.Equal(3, first.Snapshot.Obstacles.Count);
        Assert.All(first.Snapshot.Obstacles, track => Assert.Equal(ObservationSource.LocalImageSegmentation, track.ObservationSource));
        Assert.Contains(first.Snapshot.Obstacles, track => track.Lifecycle == ObstacleLifecycleState.TransientManipulator);
        Assert.Empty(preview.Read()!.Masks);

        for (var i = 0; i < 6; i++) Assert.True(await updates.MoveNextAsync());
        var settled = updates.Current.Snapshot.Obstacles;
        Assert.Contains(settled, track => track.Obstacle.Label == "container" && track.Lifecycle == ObstacleLifecycleState.SettledObstacle);
        Assert.Contains(settled, track => track.Obstacle.IsHazardZone);
    }

    private sealed class Source : IWorldPerceptionSource
    {
        internal Channel<PerceptionPublication> Queue { get; } = Channel.CreateUnbounded<PerceptionPublication>();
        public bool IsEnabled => true;
        public PerceptionSourceMode SourceMode => PerceptionSourceMode.LiveCamera;
        public System.Collections.Generic.IAsyncEnumerable<PerceptionPublication> ReadAllAsync(CancellationToken token = default) => Queue.Reader.ReadAllAsync(token);
    }

    private sealed class Server : IAsyncDisposable
    {
        private Server(WebApplication app, Uri address) { App = app; Http = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(5) }; }
        internal WebApplication App { get; }
        internal HttpClient Http { get; }
        internal Uri WebSocketUri => new UriBuilder(Http.BaseAddress!) { Scheme = "ws", Path = "/ws/twin" }.Uri;
        internal static async Task<Server> StartAsync(Source? source = null, int maximumClients = 16,
            Action<WebApplication>? configureRoutes = null)
        {
            var app = BackendApplication.Create(Array.Empty<string>(), builder =>
            {
                builder.Configuration["OmniTwin:Port"] = "0";
                builder.Configuration["OmniTwin:EnablePerception"] = "false";
                builder.Configuration["OmniTwin:MaximumClients"] = maximumClients.ToString(System.Globalization.CultureInfo.InvariantCulture);
                builder.Logging.ClearProviders();
                if (source is not null) builder.Services.AddSingleton<IWorldPerceptionSource>(source);
            });
            configureRoutes?.Invoke(app);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await app.StartAsync(timeout.Token);
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
            return new Server(app, new Uri(addresses.Addresses.Single()));
        }
        internal async Task<ClientWebSocket> ConnectAsync(string? origin = null)
        {
            var socket = new ClientWebSocket();
            socket.Options.AddSubProtocol(WorldStreamCodec.SubProtocol);
            if (origin is not null) socket.Options.SetRequestHeader("Origin", origin);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await socket.ConnectAsync(WebSocketUri, timeout.Token); return socket; }
            catch { socket.Dispose(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await App.StopAsync(stop.Token);
            await App.DisposeAsync();
        }
    }
}
