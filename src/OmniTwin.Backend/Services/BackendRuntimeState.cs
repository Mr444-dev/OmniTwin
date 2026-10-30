using System;
using OmniTwin.Backend.Streaming;
using OmniTwin.Transport;

namespace OmniTwin.Backend.Services;

/// <summary>A point-in-time operational view; readiness requires both a running source and current observations.</summary>
public sealed class BackendStatus
{
    internal BackendStatus(string state, string? reason, int clients, bool hasSnapshot, long? publicationAge,
        long? observationAge, bool current)
    { State = state; Reason = reason; ActiveClients = clients; HasSnapshot = hasSnapshot; PublicationAgeMilliseconds = publicationAge;
        ObservationAgeMilliseconds = observationAge; IsObservationCurrent = current; }
    /// <summary>Gets disabled, starting, running, degraded, stopped, or faulted.</summary>
    public string State { get; }
    /// <summary>Gets the latest sanitized failure category.</summary>
    public string? Reason { get; }
    /// <summary>Gets registered WebSocket subscribers.</summary>
    public int ActiveClients { get; }
    /// <summary>Gets whether a recent publication is available.</summary>
    public bool HasSnapshot { get; }
    /// <summary>Gets monotonic publication cache age, or null.</summary>
    public long? PublicationAgeMilliseconds { get; }
    /// <summary>Gets effective source age including cache residence, or null.</summary>
    public long? ObservationAgeMilliseconds { get; }
    /// <summary>Gets whether the source is running and the latest observations and diagnostics are current.</summary>
    public bool IsObservationCurrent { get; }
}

/// <summary>Thread-safe operational state independent of the perception task's lifetime.</summary>
public sealed class BackendRuntimeState
{
    private readonly object _gate = new object();
    private readonly SnapshotBroker _broker;
    private readonly BackendOptions _options;
    private string _state = "starting";
    private string? _reason;
    /// <summary>Creates the host state view.</summary>
    public BackendRuntimeState(SnapshotBroker broker, BackendOptions options)
    { _broker = broker; _options = options; }

    /// <summary>Changes operational mode with a sanitized reason supplied by the host.</summary>
    public void Set(string state, string? reason = null)
    {
        if (state is not ("disabled" or "starting" or "running" or "degraded" or "stopped" or "faulted")) throw new ArgumentException("Invalid runtime state.", nameof(state));
        if (reason is not null && (string.IsNullOrWhiteSpace(reason) || reason.Length > 128)) throw new ArgumentException("Invalid failure category.", nameof(reason));
        lock (_gate) { _state = state; _reason = reason; }
    }

    /// <summary>Computes effective freshness from monotonic cache age without altering the cached wire packet.</summary>
    public BackendStatus Read()
    {
        string state; string? reason;
        lock (_gate) { state = _state; reason = _reason; }
        var frame = _broker.Latest();
        long? cacheAge = frame is null ? null : _broker.AgeMilliseconds(frame);
        long? observationAge = null;
        if (frame?.Message.World.SourceAgeMilliseconds is long sourceAge && cacheAge.HasValue)
            observationAge = sourceAge > long.MaxValue - cacheAge.Value ? long.MaxValue : sourceAge + cacheAge.Value;
        var current = state == "running" && frame is not null && cacheAge.HasValue &&
            WorldStreamFreshness.IsObservationCurrent(frame.Message, 0, cacheAge.Value, frame.Message.SentAtUnixMs,
                _options.MaximumPublicationAgeMilliseconds);
        return new BackendStatus(state, reason, _broker.SubscriberCount, frame is not null, cacheAge, observationAge, current);
    }
}
