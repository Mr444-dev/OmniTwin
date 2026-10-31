using System;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using OmniTwin.Backend;
using OmniTwin.Backend.Services;
using OmniTwin.Backend.Streaming;
using OmniTwin.Contracts;
using OmniTwin.Transport;
using Xunit;

namespace OmniTwin.Backend.Tests;

public sealed class SnapshotBrokerTests
{
    private static readonly Guid Session = Guid.Parse("4d7108a6-a948-4d6a-a895-20566cfb5b80");
    private static readonly Guid Calibration = Guid.Parse("445c047b-81fa-43e9-a110-c792694a7fe3");
    internal static ObstacleTrackingSnapshot Snapshot(long sequence, long age = 0) => new(Session, Calibration, sequence,
        sequence, age, age < 3000, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), age < 3000 ? new[]
        { new TrackedObstacleState(new SegmentedObstacle(1, "cup", new Vector2D(1, 2), 0.5f, 0.9f, false),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), age, 0.8f, true) } : Array.Empty<TrackedObstacleState>());

    [Fact]
    public async Task SlowReaderRetainsOneLatestFrameWithoutBlockingTheFastReader()
    {
        var broker = new SnapshotBroker(new BackendOptions(), TimeProvider.System);
        using var slow = broker.Subscribe(); using var fast = broker.Subscribe();
        for (var sequence = 0; sequence < 1000; sequence++)
        {
            broker.Publish(Snapshot(sequence), null, 0);
            Assert.Equal(sequence, (await fast.Reader.ReadAsync(TestContext.Current.CancellationToken)).Message.SequenceNumber);
        }
        Assert.Equal(999, (await slow.Reader.ReadAsync(TestContext.Current.CancellationToken)).Message.SequenceNumber);
        Assert.False(slow.Reader.TryRead(out _));
    }

    [Fact]
    public async Task ReplaysOnlyRecentStateAndCleansUpSubscriberCapacity()
    {
        var clock = new Clock();
        var broker = new SnapshotBroker(new BackendOptions(maximumClients: 1), clock);
        broker.Publish(Snapshot(0), null, 0);
        using (var first = broker.Subscribe())
        {
            Assert.Equal(0, (await first.Reader.ReadAsync(TestContext.Current.CancellationToken)).Message.SequenceNumber);
            Assert.Throws<StreamCapacityException>(() => broker.Subscribe());
        }
        clock.Advance(2000);
        Assert.Null(broker.Latest());
        using var later = broker.Subscribe();
        Assert.False(later.Reader.TryRead(out _));
        Assert.Equal(1, broker.SubscriberCount);
        broker.Complete(true);
        Assert.True(broker.IsFaulted);
        Assert.True(broker.IsCompleted);
        Assert.Equal(0, broker.SubscriberCount);
        Assert.Null(broker.Latest());
        await later.Reader.Completion;
        Assert.Throws<InvalidOperationException>(() => broker.Subscribe());
    }

    [Fact]
    public void InvalidPublicationIsAtomicAndDoesNotConsumeStreamSequence()
    {
        var broker = new SnapshotBroker(new BackendOptions(), TimeProvider.System);
        broker.Publish(Snapshot(1), null, 0);
        Assert.Throws<WorldStreamProtocolException>(() => broker.Publish(Snapshot(0), null, 0));
        broker.Publish(Snapshot(2), null, 0);
        Assert.Equal(1, broker.Latest()!.Message.SequenceNumber);
        Assert.Equal(2, broker.Latest()!.Message.World.SequenceNumber);
    }

    [Fact]
    public void ReadinessUsesSourceAgeAndCacheAgeAndFailureDiagnostics()
    {
        var clock = new Clock();
        var options = new BackendOptions(maximumPublicationAgeMilliseconds: 10_000, retentionMilliseconds: 3000);
        var broker = new SnapshotBroker(options, clock);
        var state = new BackendRuntimeState(broker, options);
        state.Set("running");
        Assert.False(state.Read().IsObservationCurrent);
        broker.Publish(Snapshot(0, 1000), null, 0);
        Assert.True(state.Read().IsObservationCurrent);
        clock.Advance(2000);
        var aged = state.Read();
        Assert.False(aged.IsObservationCurrent);
        Assert.Equal(3000, aged.ObservationAgeMilliseconds);
        broker.Publish(Snapshot(1), "attempt_timeout", 0);
        Assert.False(state.Read().IsObservationCurrent);
        broker.Publish(Snapshot(2), null, 1);
        Assert.False(state.Read().IsObservationCurrent);
        broker.Publish(Snapshot(3), null, 0);
        state.Set("faulted", "fixture_source_failure");
        Assert.False(state.Read().IsObservationCurrent);
    }

    [Fact]
    public void InvalidSettingsFailInsteadOfFallingBack()
    {
        Assert.Throws<ArgumentException>(() => new BackendOptions(enablePerception: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackendOptions(port: -1));
        Assert.Throws<ArgumentException>(() => new BackendOptions(concept: "cup, box"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new[]
        { new System.Collections.Generic.KeyValuePair<string, string?>("OmniTwin:EnablePerception", "invalid") }).Build();
        Assert.Throws<InvalidOperationException>(() => BackendOptions.FromConfiguration(configuration));
    }

    [Fact]
    public async Task ABlockedSocketSendIsCancelledAndItsReceiveLoopIsReleased()
    {
        var options = new BackendOptions(sendTimeoutMilliseconds: 50);
        var broker = new SnapshotBroker(options, TimeProvider.System);
        using var subscription = broker.Subscribe();
        using var socket = new BlockedSocket();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = TwinStreamEndpoint.RunAsync(socket, subscription, broker, options, stop.Token, CancellationToken.None);
        broker.Publish(Snapshot(0), null, 0);
        await socket.Sending.Task.WaitAsync(stop.Token);
        for (var sequence = 1; sequence <= 10; sequence++) broker.Publish(Snapshot(sequence), null, 0);
        await run.WaitAsync(stop.Token);
        Assert.True(socket.WasAborted);
    }

    private sealed class Clock : TimeProvider
    {
        private long _milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _milliseconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(1_000_000 + _milliseconds);
        internal void Advance(long milliseconds) => _milliseconds += milliseconds;
    }

    private sealed class BlockedSocket : WebSocket
    {
        private WebSocketState _state = WebSocketState.Open;
        internal TaskCompletionSource Sending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool WasAborted { get; private set; }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string SubProtocol => WorldStreamCodec.SubProtocol;
        public override WebSocketState State => _state;
        public override void Abort() { WasAborted = true; _state = WebSocketState.Aborted; }
        public override void Dispose() => Abort();
        public override Task CloseAsync(WebSocketCloseStatus status, string? reason, CancellationToken token)
        { _state = WebSocketState.Closed; return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? reason, CancellationToken token)
        { _state = WebSocketState.CloseSent; return Task.CompletedTask; }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
        { await Task.Delay(Timeout.Infinite, token); return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true); }
        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken token)
        {
            Sending.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { Abort(); throw; }
        }
    }
}
