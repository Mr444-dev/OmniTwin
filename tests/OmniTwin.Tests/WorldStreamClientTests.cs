using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using OmniTwin.Contracts;
using OmniTwin.Transport;
using Xunit;

namespace OmniTwin.Tests;

public sealed class WorldStreamClientTests
{
    private static readonly Uri Endpoint = new Uri("ws://127.0.0.1:5108/ws/twin");
    private static readonly Guid Stream = Guid.NewGuid();
    private static readonly Guid Session = Guid.NewGuid();
    private static readonly Guid Calibration = Guid.NewGuid();

    [Fact]
    public async Task LatestStateCoalescesWithoutAConsumerAndCancellationClearsIt()
    {
        using var socket = new Socket();
        var client = new WorldStreamClient(new Connector((_, _) => Task.FromResult<WebSocket>(socket)));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = client.RunAsync(Endpoint, stop.Token);
        try
        {
            for (var i = 0; i < 100; i++) socket.Messages.Writer.TryWrite(WorldStreamCodec.Encode(Message(i)));
            await UntilAsync(() => client.GetView().State.Message?.SequenceNumber == 99);
            var view = client.GetView();
            Assert.True(view.IsObservationCurrent);
            Assert.Equal(99, view.State.Message!.SequenceNumber);
            Assert.Equal(WorldStreamClientPhase.Connected, view.State.Phase);
        }
        finally { stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); }
        Assert.Equal(WorldStreamClientPhase.Stopped, client.GetView().State.Phase);
        Assert.Null(client.GetView().State.Message);
        Assert.False(client.GetView().IsObservationCurrent);
    }

    [Fact]
    public async Task SilenceExpiresAStationaryPublicationWithoutNetworkUpdates()
    {
        using var socket = new Socket();
        var client = new WorldStreamClient(new Connector((_, _) => Task.FromResult<WebSocket>(socket)), maximumSilenceMilliseconds: 80);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = client.RunAsync(Endpoint, stop.Token);
        try
        {
            socket.Messages.Writer.TryWrite(WorldStreamCodec.Encode(Message(0)));
            await UntilAsync(() => client.GetView().State.Message is not null);
            var state = client.GetView().State;
            await Task.Delay(120, TestContext.Current.CancellationToken);
            Assert.Same(state, client.GetView().State);
            Assert.False(client.GetView().IsObservationCurrent);
        }
        finally { stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); }
    }

    [Fact]
    public async Task IndividualTracksExpireEvenWhileTheirSourceIsStillCurrent()
    {
        using var socket = new Socket();
        var client = new WorldStreamClient(new Connector((_, _) => Task.FromResult<WebSocket>(socket)));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = client.RunAsync(Endpoint, stop.Token);
        try
        {
            socket.Messages.Writer.TryWrite(WorldStreamCodec.Encode(Message(0, trackAge: 2900)));
            await UntilAsync(() => client.GetView().State.Message is not null);
            await Task.Delay(150, TestContext.Current.CancellationToken);
            var view = client.GetView();
            Assert.True(view.IsObservationCurrent);
            Assert.False(view.IsTrackCurrent(view.State.Message!.World.Obstacles[0]));
            Assert.True(view.AdditionalAgeMilliseconds >= 100);
        }
        finally { stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); }
    }

    [Fact]
    public async Task DisconnectClearsPreviouslyValidWorldAndExhaustsTheRunBudget()
    {
        using var socket = new Socket();
        var client = new WorldStreamClient(new Connector((_, _) => Task.FromResult<WebSocket>(socket)), maximumAttempts: 1);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = client.RunAsync(Endpoint, stop.Token);
        socket.Messages.Writer.TryWrite(WorldStreamCodec.Encode(Message(0)));
        await UntilAsync(() => client.GetView().State.Message is not null);
        socket.Messages.Writer.TryComplete();
        await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var view = client.GetView();
        Assert.Null(view.State.Message);
        Assert.Equal(WorldStreamClientPhase.Faulted, view.State.Phase);
        Assert.Equal("stream_ended", view.State.FailureCategory);
    }

    [Fact]
    public async Task ReconnectionDropsTheOldWorldAndStartsANewServerOrderContext()
    {
        using var first = new Socket();
        using var second = new Socket();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var connector = new Connector(async (_, token) =>
        {
            if (++calls == 1) return first;
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return second;
        });
        var client = new WorldStreamClient(connector, initialRetryDelay: TimeSpan.FromMilliseconds(5));
        var run = client.RunAsync(Endpoint, stop.Token);
        try
        {
            first.Messages.Writer.TryWrite(WorldStreamCodec.Encode(Message(10)));
            await UntilAsync(() => client.GetView().State.Message?.SequenceNumber == 10);
            first.Messages.Writer.TryComplete();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Null(client.GetView().State.Message);
            var restartedStream = Guid.NewGuid();
            second.Messages.Writer.TryWrite(WorldStreamCodec.Encode(Message(0, streamId: restartedStream)));
            release.TrySetResult();
            await UntilAsync(() => client.GetView().State.Message?.StreamId == restartedStream);
            Assert.Equal(0, client.GetView().State.Message!.SequenceNumber);
            Assert.Equal(2, client.GetView().State.Attempt);
        }
        finally { stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); }
    }

    [Fact]
    public async Task TransientFailuresHonorRetryAfterAndTheExactAttemptLimit()
    {
        var times = new ConcurrentQueue<long>();
        var clock = Stopwatch.StartNew();
        var connector = new Connector((_, _) =>
        {
            times.Enqueue(clock.ElapsedMilliseconds);
            throw new WorldStreamConnectException("handshake_503", true, TimeSpan.FromMilliseconds(50));
        });
        var client = new WorldStreamClient(connector, initialRetryDelay: TimeSpan.FromMilliseconds(5));
        await client.RunAsync(Endpoint, TestContext.Current.CancellationToken);
        var observed = times.ToArray();
        Assert.Equal(3, connector.Calls);
        Assert.True(observed[1] - observed[0] >= 40);
        Assert.True(observed[2] - observed[1] >= 40);
        Assert.Equal(3, client.GetView().State.Attempt);
        Assert.Equal("handshake_503", client.GetView().State.FailureCategory);
    }

    [Fact]
    public async Task PermanentHandshakeRejectionDoesNotRetryAndCanBeRestartedExplicitly()
    {
        var connector = new Connector((_, _) => throw new WorldStreamConnectException("handshake_401", false));
        var client = new WorldStreamClient(connector);
        await client.RunAsync(Endpoint, TestContext.Current.CancellationToken);
        var firstRevision = client.GetView().State.Revision;
        Assert.Equal(1, connector.Calls);
        await client.RunAsync(Endpoint, TestContext.Current.CancellationToken);
        Assert.Equal(2, connector.Calls);
        Assert.True(client.GetView().State.Revision > firstRevision);
    }

    [Fact]
    public async Task InvalidProtocolTerminatesWithoutPublishingOrReconnecting()
    {
        using var socket = new Socket();
        socket.Messages.Writer.TryWrite(new byte[] { (byte)'{', (byte)'}' });
        var connector = new Connector((_, _) => Task.FromResult<WebSocket>(socket));
        var client = new WorldStreamClient(connector);
        await client.RunAsync(Endpoint, TestContext.Current.CancellationToken);
        Assert.Equal(1, connector.Calls);
        Assert.Null(client.GetView().State.Message);
        Assert.Equal(WorldStreamClientPhase.Faulted, client.GetView().State.Phase);
        Assert.Equal(WebSocketState.Aborted, socket.State);
    }

    [Fact]
    public async Task ConnectTimeoutIsBoundedAndConcurrentRunsAreRejected()
    {
        var connector = new Connector(async (_, token) =>
        { await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException("unreachable"); });
        var client = new WorldStreamClient(connector, maximumAttempts: 1, connectTimeout: TimeSpan.FromMilliseconds(50));
        var first = client.RunAsync(Endpoint, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.RunAsync(Endpoint, TestContext.Current.CancellationToken));
        await first;
        Assert.Equal("connect_timeout", client.GetView().State.FailureCategory);
    }

    [Fact]
    public async Task CancellationInterruptsBackoffAndUnexpectedFailuresAreObservable()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var client = new WorldStreamClient(new Connector((_, _) => throw new WorldStreamConnectException("network", true)),
            initialRetryDelay: TimeSpan.FromSeconds(10));
        var run = client.RunAsync(Endpoint, stop.Token);
        await UntilAsync(() => client.GetView().State.Phase == WorldStreamClientPhase.Reconnecting);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(WorldStreamClientPhase.Stopped, client.GetView().State.Phase);
        var broken = new WorldStreamClient(new Connector((_, _) => throw new InvalidOperationException("fixture")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => broken.RunAsync(Endpoint, TestContext.Current.CancellationToken));
        Assert.Equal("unexpected_client_failure", broken.GetView().State.FailureCategory);
    }

    [Fact]
    public async Task ValidatesEndpointAndFiniteBudgetsBeforeStarting()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorldStreamClient(maximumAttempts: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorldStreamClient(connectTimeout: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorldStreamClient(maximumSilenceMilliseconds: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorldStreamConnectException("network", true, TimeSpan.FromMinutes(2)));
        var client = new WorldStreamClient();
        await Assert.ThrowsAsync<ArgumentException>(() => client.RunAsync(new Uri("https://example.com"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => client.RunAsync(new Uri("ws://name:secret@example.com"), TestContext.Current.CancellationToken));
        Assert.Equal(WorldStreamClientPhase.Stopped, client.GetView().State.Phase);
    }

    private static WorldStreamMessage Message(long sequence, long trackAge = 100, Guid? streamId = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var track = new TrackedObstacleState(new SegmentedObstacle(1, "cup", new Vector2D(2, 3), 0.5f, 0.9f, false), now - trackAge, trackAge, 0.8f, true);
        var world = new ObstacleTrackingSnapshot(Session, Calibration, sequence, sequence, 50, true, now, new[] { track });
        return new WorldStreamMessage(streamId ?? Stream, sequence, now, 3000, world, null, 0);
    }
    private static async Task UntilAsync(Func<bool> condition)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, stop.Token);
    }
    private sealed class Connector : IWorldStreamConnector
    {
        private readonly Func<Uri, CancellationToken, Task<WebSocket>> _connect;
        internal Connector(Func<Uri, CancellationToken, Task<WebSocket>> connect) => _connect = connect;
        internal int Calls;
        public Task<WebSocket> ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); return _connect(endpoint, cancellationToken); }
    }
    private sealed class Socket : WebSocket
    {
        internal readonly Channel<byte[]> Messages = Channel.CreateUnbounded<byte[]>();
        private WebSocketState _state = WebSocketState.Open;
        public override WebSocketCloseStatus? CloseStatus => WebSocketCloseStatus.NormalClosure;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string SubProtocol => WorldStreamCodec.SubProtocol;
        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus status, string? reason, CancellationToken token) { _state = WebSocketState.Closed; return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? reason, CancellationToken token) { _state = WebSocketState.Closed; return Task.CompletedTask; }
        public override Task SendAsync(ArraySegment<byte> bytes, WebSocketMessageType type, bool end, CancellationToken token) => throw new NotSupportedException();
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> bytes, CancellationToken token)
        {
            if (!await Messages.Reader.WaitToReadAsync(token))
            { _state = WebSocketState.CloseReceived; return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, null); }
            var payload = await Messages.Reader.ReadAsync(token);
            payload.CopyTo(bytes.Array!, bytes.Offset);
            return new WebSocketReceiveResult(payload.Length, WebSocketMessageType.Text, true);
        }
    }
}
