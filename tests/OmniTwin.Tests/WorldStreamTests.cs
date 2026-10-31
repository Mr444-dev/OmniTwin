using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using OmniTwin.Contracts;
using OmniTwin.Transport;
using Xunit;

namespace OmniTwin.Tests;

public sealed class WorldStreamTests
{
    private static readonly Guid Stream = Guid.Parse("ebbbef0d-5c68-4083-acb3-6f6c9f698965");
    private static readonly Guid Session = Guid.Parse("4d7108a6-a948-4d6a-a895-20566cfb5b80");
    private static readonly Guid Calibration = Guid.Parse("445c047b-81fa-43e9-a110-c792694a7fe3");
    private static WorldStreamMessage Message(long sequence = 0, long? worldSequence = null, Guid? stream = null,
        Guid? session = null, Guid? calibration = null, long? source = 3, long? sourceAge = 50,
        string? failure = null, int rejected = 0)
    {
        var states = sourceAge.HasValue && sourceAge < 3000 ? new[] { new TrackedObstacleState(
            new SegmentedObstacle(7, "cup", new Vector2D(1, 2), 0.5f, 0.9f, false), 999900,
            Math.Max(100, sourceAge.Value), 0.8f, true) } : Array.Empty<TrackedObstacleState>();
        var snapshot = new ObstacleTrackingSnapshot(session ?? Session, calibration ?? Calibration, worldSequence ?? sequence,
            source, sourceAge, sourceAge.HasValue && sourceAge < 3000, 1_000_000, states);
        return new WorldStreamMessage(stream ?? Stream, sequence, 1_000_000, 3000, snapshot, failure, rejected,
            sourceMode: PerceptionSourceMode.LiveCamera);
    }

    [Fact]
    public void CodecRoundTripsEveryFieldUsingTheSameCamelCaseBytes()
    {
        var original = Message();
        var bytes = WorldStreamCodec.Encode(original);
        var parsed = WorldStreamCodec.Decode(bytes);
        Assert.Equal(bytes, WorldStreamCodec.Encode(parsed));
        Assert.Equal(original.World.Obstacles[0], parsed.World.Obstacles[0]);
        Assert.Equal(Stream, parsed.StreamId);
        Assert.Contains("\"messageType\":\"world.perception\"", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void CodecRejectsUnknownDuplicateMissingVersionAndScalarErrors()
    {
        var json = Encoding.UTF8.GetString(WorldStreamCodec.Encode(Message()));
        var invalid = new[]
        {
            json.Replace("\"protocolVersion\":3", "\"protocolVersion\":2"),
            json.Replace("\"protocolVersion\":3", "\"protocolVersion\":3,\"extra\":0"),
            json.Replace("\"protocolVersion\":3", "\"protocolVersion\":3,\"protocolVersion\":3"),
            json.Replace("\"protocolVersion\":3,", ""),
            json.Replace("\"sourceMode\":0", "\"sourceMode\":99"),
            json.Replace("\"radiusMeters\":0.5", "\"radiusMeters\":0"),
            json.Replace("\"x\":1", "\"x\":1,\"x\":2"),
            json.Replace("\"confidence\":0.9", "\"confidence\":1e9999"),
            json.Replace("\"isObservationCurrent\":true", "\"isObservationCurrent\":false"),
            json.Replace("\"sequenceNumber\":0", "\"sequenceNumber\":-1"),
            json.Replace("\"label\":\"cup\"", "\"label\":null"),
            json.Replace("\"sourceAgeMilliseconds\":50", "\"sourceAgeMilliseconds\":200"),
            json.Replace("world.perception", "unknown.message"),
            "{}", "[1,2]", "{", "null"
        };
        foreach (var invalidJson in invalid)
            Assert.Throws<WorldStreamProtocolException>(() => WorldStreamCodec.Decode(Encoding.UTF8.GetBytes(invalidJson)));
        Assert.Throws<WorldStreamProtocolException>(() => WorldStreamCodec.Decode(new byte[WorldStreamCodec.MaximumMessageBytes + 1]));
        Assert.Throws<WorldStreamProtocolException>(() => WorldStreamCodec.Decode(new byte[] { 255, 255 }));
    }

    [Fact]
    public void WireContractRejectsExpiredOrInconsistentTrackMetadata()
    {
        var state = new TrackedObstacleState(new SegmentedObstacle(1, "cup", default, 1, 0.9f, false), 1000, 3000, 0, false);
        var snapshot = new ObstacleTrackingSnapshot(Session, Calibration, 1, 1, 3000, false, 4000, new[] { state });
        Assert.Throws<ArgumentException>(() => new WorldStreamMessage(Stream, 0, 4000, 3000, snapshot, null, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorldStreamMessage(Stream, 0, 4000, 3000, Message().World, null, 129));
        Assert.Throws<ArgumentException>(() => new WorldStreamMessage(Guid.Empty, 0, 4000, 3000, Message().World, null, 0));
    }

    [Fact]
    public void OrderGuardAllowsCoalescingGapsAndRejectsReplaysWithoutAdvancingState()
    {
        var guard = new WorldStreamOrderGuard();
        guard.Accept(Message(0));
        guard.Accept(Message(7, sourceAge: 70));
        Assert.Throws<WorldStreamProtocolException>(() => guard.Accept(Message(7, sourceAge: 80)));
        Assert.Throws<WorldStreamProtocolException>(() => guard.Accept(Message(8, worldSequence: 6)));
        Assert.Throws<WorldStreamProtocolException>(() => guard.Accept(Message(8, source: 2)));
        Assert.Throws<WorldStreamProtocolException>(() => guard.Accept(Message(8, sourceAge: 60)));
        guard.Accept(Message(8, sourceAge: 80));
    }

    [Fact]
    public void SessionGuardRejectsCalibrationMutationStreamMutationAndRetiredSessions()
    {
        var guard = new WorldStreamOrderGuard();
        guard.Accept(Message());
        Assert.Throws<WorldStreamProtocolException>(() => guard.Accept(Message(1, calibration: Guid.NewGuid())));
        Assert.Throws<WorldStreamProtocolException>(() => guard.Accept(Message(1, stream: Guid.NewGuid())));
        guard.Accept(Message(1, worldSequence: 0, session: Guid.NewGuid(), calibration: Guid.NewGuid()));
        Assert.Throws<WorldStreamProtocolException>(() => guard.Accept(Message(2)));
    }

    [Fact]
    public void StreamProvenanceIsExplicitAndCannotSwitchWithoutRestart()
    {
        var guard = new WorldStreamOrderGuard();
        var live = Message();
        guard.Accept(live);
        var replay = new WorldStreamMessage(Stream, 1, 1_000_100, 3000,
            new ObstacleTrackingSnapshot(Session, Calibration, 1, 4, 60, true, 1_000_100,
                live.World.Obstacles), null, 0, sourceMode: PerceptionSourceMode.SyntheticTabletopReplay);
        Assert.Throws<WorldStreamProtocolException>(() => guard.Accept(replay));
        var decoded = WorldStreamCodec.Decode(WorldStreamCodec.Encode(live));
        Assert.Equal(PerceptionSourceMode.LiveCamera, decoded.SourceMode);
        Assert.Equal(ObstacleLifecycleState.SettledObstacle, Assert.Single(decoded.World.Obstacles).Lifecycle);
    }

    [Fact]
    public void FreshnessIncludesDeliveryLocalSilenceFailuresAndSourceRetention()
    {
        var message = Message();
        Assert.True(WorldStreamFreshness.IsObservationCurrent(message, 100, 200, 1_000_100));
        Assert.False(WorldStreamFreshness.IsObservationCurrent(message, 100, 2100, 1_000_100));
        Assert.False(WorldStreamFreshness.IsObservationCurrent(message, 0, 100, 1_003_000));
        Assert.False(WorldStreamFreshness.IsObservationCurrent(message, 0, 0, 999_499));
        Assert.False(WorldStreamFreshness.IsObservationCurrent(Message(failure: "attempt_timeout"), 0, 0, 1_000_000));
        Assert.False(WorldStreamFreshness.IsObservationCurrent(Message(rejected: 1), 0, 0, 1_000_000));
        Assert.False(WorldStreamFreshness.IsObservationCurrent(Message(source: null, sourceAge: null), 0, 0, 1_000_000));
        Assert.False(WorldStreamFreshness.IsObservationCurrent(Message(sourceAge: 3000), 0, 0, 1_000_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorldStreamFreshness.IsObservationCurrent(message, 100, 99, 1_000_000));
    }

    [Fact]
    public async Task ReceiverReassemblesFragmentsAndAcknowledgesGracefulClose()
    {
        var bytes = WorldStreamCodec.Encode(Message());
        using var socket = new ScriptedSocket(new[]
        {
            new Segment(bytes.Take(10).ToArray(), false), new Segment(bytes.Skip(10).ToArray(), true), Segment.Close()
        });
        var messages = new List<WorldStreamMessage>();
        await foreach (var message in new WorldStreamReceiver().ReadAllAsync(socket, TestContext.Current.CancellationToken)) messages.Add(message);
        Assert.Single(messages);
        Assert.Equal(1, socket.CloseAcknowledgements);
    }

    [Fact]
    public async Task ReceiverRejectsInvalidProtocolBinaryPayloadAndFragmentLimit()
    {
        var cases = new[]
        {
            new ScriptedSocket(new[] { new Segment(new byte[] { 1 }, true, WebSocketMessageType.Binary) }),
            new ScriptedSocket(new[] { new Segment(Array.Empty<byte>(), false), new Segment(Array.Empty<byte>(), false) }),
            new ScriptedSocket(new[] { new Segment(Encoding.UTF8.GetBytes("{}"), true) }),
            new ScriptedSocket(Array.Empty<Segment>(), "unsupported")
        };
        foreach (var socket in cases)
        {
            using (socket)
            {
                await Assert.ThrowsAsync<WorldStreamProtocolException>(async () =>
                {
                    await foreach (var _ in new WorldStreamReceiver(maximumFragments: 1).ReadAllAsync(socket, TestContext.Current.CancellationToken))
                        Assert.Fail("Invalid frames must not reach consumers.");
                });
            }
        }
    }

    [Fact]
    public async Task ReceiverRejectsOversizedMessagesAndReplayedPublications()
    {
        var oversized = Enumerable.Range(0, 33).Select(_ => new Segment(new byte[8192], false)).ToArray();
        using var large = new ScriptedSocket(oversized);
        await Assert.ThrowsAsync<WorldStreamProtocolException>(async () =>
        { await foreach (var _ in new WorldStreamReceiver().ReadAllAsync(large, TestContext.Current.CancellationToken)) Assert.Fail("Oversized message emitted."); });
        var bytes = WorldStreamCodec.Encode(Message());
        using var replay = new ScriptedSocket(new[] { new Segment(bytes, true), new Segment(bytes, true) });
        await using var reader = new WorldStreamReceiver().ReadAllAsync(replay, TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await reader.MoveNextAsync());
        await Assert.ThrowsAsync<WorldStreamProtocolException>(() => reader.MoveNextAsync().AsTask());
    }

    [Fact]
    public async Task ReceiverEnforcesWholeMessageTimeoutAndCallerCancellation()
    {
        using var idle = new ScriptedSocket(Array.Empty<Segment>());
        var error = await Assert.ThrowsAsync<WorldStreamConnectionException>(async () =>
        { await foreach (var _ in new WorldStreamReceiver(TimeSpan.FromMilliseconds(30)).ReadAllAsync(idle, TestContext.Current.CancellationToken)) Assert.Fail("Idle stream emitted data."); });
        Assert.Equal("receive_timeout", error.Category);
        using var cancelled = new ScriptedSocket(Array.Empty<Segment>());
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        { await foreach (var _ in new WorldStreamReceiver().ReadAllAsync(cancelled, stop.Token)) Assert.Fail("Cancelled stream emitted data."); });
    }

    private sealed class Segment
    {
        internal Segment(byte[] bytes, bool end, WebSocketMessageType type = WebSocketMessageType.Text)
        { Bytes = bytes; End = end; Type = type; }
        internal byte[] Bytes { get; }
        internal bool End { get; }
        internal WebSocketMessageType Type { get; }
        internal static Segment Close() => new(Array.Empty<byte>(), true, WebSocketMessageType.Close);
    }

    private sealed class ScriptedSocket : WebSocket
    {
        private readonly Channel<Segment> _segments = Channel.CreateUnbounded<Segment>();
        private readonly string _protocol;
        private readonly CancellationTokenSource _abort = new CancellationTokenSource();
        private WebSocketState _state = WebSocketState.Open;
        internal ScriptedSocket(IEnumerable<Segment> segments, string protocol = WorldStreamCodec.SubProtocol)
        { _protocol = protocol; foreach (var segment in segments) _segments.Writer.TryWrite(segment); }
        internal int CloseAcknowledgements { get; private set; }
        public override WebSocketCloseStatus? CloseStatus => WebSocketCloseStatus.NormalClosure;
        public override string? CloseStatusDescription => null;
        public override string SubProtocol => _protocol;
        public override WebSocketState State => _state;
        public override void Abort() { _state = WebSocketState.Aborted; _abort.Cancel(); }
        public override void Dispose() { Abort(); _abort.Dispose(); }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken token) => CloseOutputAsync(closeStatus, statusDescription, token);
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken token)
        { token.ThrowIfCancellationRequested(); CloseAcknowledgements++; _state = WebSocketState.Closed; return Task.CompletedTask; }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, _abort.Token);
            var segment = await _segments.Reader.ReadAsync(stop.Token);
            Assert.True(segment.Bytes.Length <= buffer.Count);
            Array.Copy(segment.Bytes, 0, buffer.Array!, buffer.Offset, segment.Bytes.Length);
            if (segment.Type == WebSocketMessageType.Close) _state = WebSocketState.CloseReceived;
            return new WebSocketReceiveResult(segment.Bytes.Length, segment.Type, segment.End,
                segment.Type == WebSocketMessageType.Close ? CloseStatus : null, null);
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    }
}
