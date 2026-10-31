using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using OmniTwin.Contracts;
using OmniTwin.Transport;

namespace OmniTwin.Backend.Streaming;

/// <summary>A serialized publication shared read-only by all connection writers.</summary>
public sealed class SerializedWorldFrame
{
    internal SerializedWorldFrame(WorldStreamMessage message, byte[] ownedBytes, long acceptedTimestamp)
    { Message = message; Bytes = ownedBytes; AcceptedTimestamp = acceptedTimestamp; }
    /// <summary>Gets the immutable wire message.</summary>
    public WorldStreamMessage Message { get; }
    internal ReadOnlyMemory<byte> Bytes { get; }
    internal long AcceptedTimestamp { get; }
}

/// <summary>A bounded, independently disposable stream subscription.</summary>
public sealed class WorldStreamSubscription : IDisposable
{
    private readonly SnapshotBroker _owner;
    private int _disposed;
    internal WorldStreamSubscription(SnapshotBroker owner, Guid id, ChannelReader<SerializedWorldFrame> reader)
    { _owner = owner; Id = id; Reader = reader; }
    internal Guid Id { get; }
    /// <summary>Gets the capacity-one stream; intermediate pending publications may be replaced.</summary>
    public ChannelReader<SerializedWorldFrame> Reader { get; }
    /// <summary>Releases the subscriber slot and ends its queue.</summary>
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _owner.Unsubscribe(Id); }
}

/// <summary>Serializes each snapshot once and fans it out without waiting for network clients.</summary>
public sealed class SnapshotBroker
{
    private readonly object _gate = new object();
    private readonly BackendOptions _options;
    private readonly TimeProvider _time;
    private readonly Guid _streamId = Guid.NewGuid();
    private readonly Dictionary<Guid, Channel<SerializedWorldFrame>> _subscribers = new Dictionary<Guid, Channel<SerializedWorldFrame>>();
    private readonly WorldStreamOrderGuard _order = new WorldStreamOrderGuard();
    private SerializedWorldFrame? _latest;
    private long _sequence;
    private bool _completed;
    private bool _faulted;

    /// <summary>Creates a run-scoped stream with bounded subscriber count and replay age.</summary>
    public SnapshotBroker(BackendOptions options, TimeProvider timeProvider)
    { _options = options ?? throw new ArgumentNullException(nameof(options)); _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)); }
    /// <summary>Gets the number of registered connection slots.</summary>
    public int SubscriberCount { get { lock (_gate) return _subscribers.Count; } }
    /// <summary>Gets whether the stream has terminated with a perception failure.</summary>
    public bool IsFaulted { get { lock (_gate) return _faulted; } }
    /// <summary>Gets whether this run-scoped broker has ended.</summary>
    public bool IsCompleted { get { lock (_gate) return _completed; } }

    /// <summary>Validates and publishes atomically. No socket I/O or asynchronous wait runs in this method.</summary>
    public void Publish(ObstacleTrackingSnapshot snapshot, string? failureCategory, int rejectedDetectionCount)
        => Publish(snapshot, failureCategory, rejectedDetectionCount, PerceptionSourceMode.LiveCamera);

    /// <summary>Validates and publishes a snapshot with its explicit live or replay provenance.</summary>
    public void Publish(ObstacleTrackingSnapshot snapshot, string? failureCategory, int rejectedDetectionCount, PerceptionSourceMode sourceMode)
    {
        lock (_gate)
        {
            if (_completed) throw new InvalidOperationException("The stream has ended.");
            if (_sequence == long.MaxValue) throw new InvalidOperationException("Stream sequence exhausted.");
            var message = new WorldStreamMessage(_streamId, _sequence, _time.GetUtcNow().ToUnixTimeMilliseconds(),
                _options.RetentionMilliseconds, snapshot, failureCategory, rejectedDetectionCount, sourceMode: sourceMode);
            var bytes = WorldStreamCodec.Encode(message);
            _order.Accept(message);
            var frame = new SerializedWorldFrame(message, bytes, _time.GetTimestamp());
            _latest = frame; _sequence++;
            foreach (var subscriber in _subscribers.Values) subscriber.Writer.TryWrite(frame);
        }
    }

    /// <summary>Reserves a bounded slot and replays the latest publication only when it is recent enough.</summary>
    public WorldStreamSubscription Subscribe()
    {
        lock (_gate)
        {
            if (_completed) throw new InvalidOperationException("The stream has ended.");
            if (_subscribers.Count >= _options.MaximumClients) throw new StreamCapacityException();
            var channel = Channel.CreateBounded<SerializedWorldFrame>(new BoundedChannelOptions(1)
            { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });
            var id = Guid.NewGuid(); _subscribers.Add(id, channel);
            if (Recent(_latest)) channel.Writer.TryWrite(_latest!);
            return new WorldStreamSubscription(this, id, channel.Reader);
        }
    }

    /// <summary>Gets a recent cached publication, or null when absent, expired, or terminated.</summary>
    public SerializedWorldFrame? Latest() { lock (_gate) return !_completed && Recent(_latest) ? _latest : null; }

    /// <summary>Gets cache age from the host's monotonic clock.</summary>
    public long AgeMilliseconds(SerializedWorldFrame frame) =>
        (long)_time.GetElapsedTime(frame.AcceptedTimestamp, _time.GetTimestamp()).TotalMilliseconds;

    /// <summary>Clears cached state, releases subscriber slots, and completes every pending stream.</summary>
    public void Complete(bool faulted)
    {
        lock (_gate)
        {
            if (_completed) return;
            _completed = true; _faulted = faulted; _latest = null;
            foreach (var channel in _subscribers.Values) channel.Writer.TryComplete();
            _subscribers.Clear();
        }
    }

    internal void Unsubscribe(Guid id)
    {
        lock (_gate)
        {
            if (_subscribers.Remove(id, out var channel)) channel.Writer.TryComplete();
        }
    }
    private bool Recent(SerializedWorldFrame? frame)
    {
        if (frame is null) return false;
        var age = AgeMilliseconds(frame);
        return age >= 0 && age < _options.MaximumPublicationAgeMilliseconds;
    }
}

/// <summary>The configured subscriber capacity has been reached.</summary>
public sealed class StreamCapacityException : InvalidOperationException
{
    /// <summary>Creates the capacity failure.</summary>
    public StreamCapacityException() : base("World stream subscriber capacity reached.") { }
}
