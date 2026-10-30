using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using OmniTwin.Contracts;

namespace OmniTwin.Transport;

/// <summary>Describes the connection independently of whether observations are still current.</summary>
public enum WorldStreamClientPhase
{
    /// <summary>No connection loop is running.</summary>
    Stopped,
    /// <summary>A bounded connection attempt is running.</summary>
    Connecting,
    /// <summary>The protocol is connected; observations can independently be absent or stale.</summary>
    Connected,
    /// <summary>A transient failure is waiting for its bounded retry delay.</summary>
    Reconnecting,
    /// <summary>The operation ended with a permanent failure or exhausted attempt budget.</summary>
    Faulted
}

/// <summary>An immutable atomic handoff from a single network producer to any number of readers.</summary>
public sealed class WorldStreamClientState
{
    internal WorldStreamClientState(long revision, WorldStreamClientPhase phase, int attempt,
        string? failureCategory, WorldStreamMessage? message = null, long receivedElapsedMs = 0, long receivedAtUnixMs = 0)
    {
        Revision = revision; Phase = phase; Attempt = attempt; FailureCategory = failureCategory;
        Message = message; ReceivedElapsedMilliseconds = receivedElapsedMs; ReceivedAtUnixMs = receivedAtUnixMs;
    }
    /// <summary>Gets a monotonically increasing local state revision, including lifecycle changes.</summary>
    public long Revision { get; }
    /// <summary>Gets the transport lifecycle phase.</summary>
    public WorldStreamClientPhase Phase { get; }
    /// <summary>Gets the one-based connection attempt within this run, or zero before a run.</summary>
    public int Attempt { get; }
    /// <summary>Gets a sanitized terminal/transient category, never an exception or peer reason.</summary>
    public string? FailureCategory { get; }
    /// <summary>Gets the most recent validated message for this connection; disconnected states have none.</summary>
    public WorldStreamMessage? Message { get; }
    /// <summary>Gets the local monotonic reception time on the owning client's clock.</summary>
    public long ReceivedElapsedMilliseconds { get; }
    /// <summary>Gets the local wall-clock reception time used to estimate delivery age.</summary>
    public long ReceivedAtUnixMs { get; }
}

/// <summary>A read-time view that reevaluates freshness even when no new messages arrive.</summary>
public readonly struct WorldStreamClientView
{
    internal WorldStreamClientView(WorldStreamClientState state, bool current, long additionalAge)
    { State = state; IsObservationCurrent = current; AdditionalAgeMilliseconds = additionalAge; }
    /// <summary>Gets the immutable handoff state.</summary>
    public WorldStreamClientState State { get; }
    /// <summary>Gets effective source/delivery/cache freshness at the time this view was read.</summary>
    public bool IsObservationCurrent { get; }
    /// <summary>Gets bounded delivery plus local cache age to add to individual track ages.</summary>
    public long AdditionalAgeMilliseconds { get; }
    /// <summary>Checks one track from this view, including expiry between network publications.</summary>
    public bool IsTrackCurrent(TrackedObstacleState track)
    {
        if (track is null) throw new ArgumentNullException(nameof(track));
        return IsObservationCurrent && State.Message is not null &&
            track.AgeMilliseconds < State.Message.RetentionMilliseconds - AdditionalAgeMilliseconds;
    }
}

/// <summary>A classified handshake failure with bounded server retry advice.</summary>
public sealed class WorldStreamConnectException : IOException
{
    /// <summary>Creates a sanitized failure with a transient/permanent classification.</summary>
    public WorldStreamConnectException(string category, bool isTransient, TimeSpan? retryAfter = null) : base(category)
    {
        if (string.IsNullOrWhiteSpace(category) || category.Length > 128) throw new ArgumentException("A bounded category is required.", nameof(category));
        if (retryAfter.HasValue && (retryAfter.Value < TimeSpan.Zero || retryAfter.Value > TimeSpan.FromMinutes(1)))
            throw new ArgumentOutOfRangeException(nameof(retryAfter));
        Category = category; IsTransient = isTransient; RetryAfter = retryAfter;
    }
    /// <summary>Gets the stable failure category.</summary>
    public string Category { get; }
    /// <summary>Gets whether a bounded retry is permitted.</summary>
    public bool IsTransient { get; }
    /// <summary>Gets the minimum advised retry delay, if supplied.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>Establishes a negotiated socket; ownership transfers to the caller on success.</summary>
public interface IWorldStreamConnector
{
    /// <summary>Connects using omnitwin.v1; disposes failed sockets and honors cancellation.</summary>
    Task<WebSocket> ConnectAsync(Uri endpoint, CancellationToken cancellationToken);
}

/// <summary>A desktop ClientWebSocket adapter; unknown handshake failures fail closed on portable runtimes.</summary>
public sealed class ClientWebSocketConnector : IWorldStreamConnector
{
    /// <inheritdoc />
    public async Task<WebSocket> ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(WorldStreamCodec.SubProtocol);
#if NET10_0_OR_GREATER
        socket.Options.CollectHttpResponseDetails = true;
#endif
        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch (WebSocketException error)
        {
            var status = 0;
            string? retryAfter = null;
#if NET10_0_OR_GREATER
            status = (int)socket.HttpStatusCode;
            if (socket.HttpResponseHeaders is not null && socket.HttpResponseHeaders.TryGetValue("Retry-After", out var values))
                foreach (var value in values) { retryAfter = value; break; }
#else
            for (Exception? inner = error; inner is not null; inner = inner.InnerException)
                if (inner is WebException web && web.Response is HttpWebResponse response)
                { status = (int)response.StatusCode; retryAfter = response.Headers["Retry-After"]; response.Dispose(); break; }
#endif
            var network = false;
            for (Exception? inner = error; inner is not null; inner = inner.InnerException)
                if (inner is SocketException socketError && (socketError.SocketErrorCode == SocketError.ConnectionRefused ||
                    socketError.SocketErrorCode == SocketError.TimedOut || socketError.SocketErrorCode == SocketError.ConnectionReset ||
                    socketError.SocketErrorCode == SocketError.NetworkUnreachable || socketError.SocketErrorCode == SocketError.HostUnreachable)) network = true;
            var transient = status == 408 || status == 429 || status == 500 || status == 502 || status == 503 || status == 504 || (status == 0 && network);
            TimeSpan? advised = null;
            if (retryAfter is not null)
            {
                if (long.TryParse(retryAfter, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
                {
                    if (seconds > 60) throw new WorldStreamConnectException("retry_after_exceeds_budget", false);
                    advised = TimeSpan.FromSeconds(seconds);
                }
                else if (DateTimeOffset.TryParse(retryAfter, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var date))
                {
                    var delay = date - DateTimeOffset.UtcNow;
                    if (delay > TimeSpan.FromMinutes(1)) throw new WorldStreamConnectException("retry_after_exceeds_budget", false);
                    advised = delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
                }
            }
            throw new WorldStreamConnectException(status == 0 ? "connect_unavailable_or_unclassified" : "handshake_" + status,
                transient, advised);
        }
        finally { if (socket.State != WebSocketState.Open) socket.Dispose(); }
    }
}

/// <summary>Runs bounded connection attempts and publishes only the latest immutable validated state.</summary>
/// <remarks>No Unity API or user callback runs on the producer. Call GetView on the presentation thread.</remarks>
public sealed class WorldStreamClient
{
    private readonly IWorldStreamConnector _connector;
    private readonly int _maximumAttempts;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _receiveTimeout;
    private readonly TimeSpan _initialRetryDelay;
    private readonly long _maximumSilenceMs;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private WorldStreamClientState _state = new WorldStreamClientState(0, WorldStreamClientPhase.Stopped, 0, null);
    private long _revision;
    private int _running;

    /// <summary>Sets finite budgets. The attempt count covers the whole run, including reconnects after a successful connection.</summary>
    public WorldStreamClient(IWorldStreamConnector? connector = null, int maximumAttempts = 3,
        TimeSpan? connectTimeout = null, TimeSpan? receiveTimeout = null, TimeSpan? initialRetryDelay = null,
        long maximumSilenceMilliseconds = 2000)
    {
        if (maximumAttempts < 1 || maximumAttempts > 10) throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        _connectTimeout = Validate(connectTimeout ?? TimeSpan.FromSeconds(5), nameof(connectTimeout));
        _receiveTimeout = Validate(receiveTimeout ?? TimeSpan.FromSeconds(10), nameof(receiveTimeout));
        _initialRetryDelay = Validate(initialRetryDelay ?? TimeSpan.FromMilliseconds(250), nameof(initialRetryDelay));
        if (maximumSilenceMilliseconds < 1 || maximumSilenceMilliseconds > 60_000) throw new ArgumentOutOfRangeException(nameof(maximumSilenceMilliseconds));
        _connector = connector ?? new ClientWebSocketConnector(); _maximumAttempts = maximumAttempts;
        _maximumSilenceMs = maximumSilenceMilliseconds;
    }

    /// <summary>Reads one atomic state and reevaluates age without locks, network work, or blocking.</summary>
    public WorldStreamClientView GetView()
    {
        var state = Volatile.Read(ref _state);
        var now = _clock.ElapsedMilliseconds;
        var current = state.Phase == WorldStreamClientPhase.Connected && state.Message is not null &&
            WorldStreamFreshness.IsObservationCurrent(state.Message, state.ReceivedElapsedMilliseconds,
                now, state.ReceivedAtUnixMs, _maximumSilenceMs);
        var additionalAge = state.Message is null ? 0 : Math.Min(60_000, now - state.ReceivedElapsedMilliseconds) +
            Math.Min(60_000, Math.Max(0, state.ReceivedAtUnixMs - state.Message.SentAtUnixMs));
        return new WorldStreamClientView(state, current, additionalAge);
    }

    /// <summary>Runs a single producer until cancellation or a classified terminal failure. Restart explicitly after completion.</summary>
    /// <remarks>Cancellation propagates after publishing Stopped; unexpected programmer/runtime failures publish Faulted and propagate.</remarks>
    public async Task RunAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        if (endpoint is null || !endpoint.IsAbsoluteUri || (endpoint.Scheme != "ws" && endpoint.Scheme != "wss") ||
            endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0)
            throw new ArgumentException("Use an absolute WebSocket URL without credentials or a fragment.", nameof(endpoint));
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) throw new InvalidOperationException("Only one client producer may run.");
        var attempt = 0;
        try
        {
            for (attempt = 1; attempt <= _maximumAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Publish(WorldStreamClientPhase.Connecting, attempt, null);
                string category;
                var transient = true;
                TimeSpan? advised = null;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(_connectTimeout);
                    using var socket = await _connector.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
                    timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                    try
                    {
                        Publish(WorldStreamClientPhase.Connected, attempt, null);
                        await foreach (var message in new WorldStreamReceiver(_receiveTimeout).ReadAllAsync(socket, cancellationToken).ConfigureAwait(false))
                            Publish(WorldStreamClientPhase.Connected, attempt, null, message);
                        category = "stream_ended";
                    }
                    finally { if (socket.State != WebSocketState.Closed) socket.Abort(); }
                }
                catch (WorldStreamConnectException failure)
                { category = failure.Category; transient = failure.IsTransient; advised = failure.RetryAfter; }
                catch (WorldStreamProtocolException failure) { category = failure.Category; transient = false; }
                catch (WorldStreamConnectionException failure) { category = failure.Category; }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { category = "connect_timeout"; }
                cancellationToken.ThrowIfCancellationRequested();
                if (!transient || attempt == _maximumAttempts)
                { Publish(WorldStreamClientPhase.Faulted, attempt, category); return; }
                Publish(WorldStreamClientPhase.Reconnecting, attempt, category);
                var delay = TimeSpan.FromMilliseconds(Math.Min(60_000, _initialRetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1)));
                if (advised > delay) delay = advised.Value;
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { Publish(WorldStreamClientPhase.Stopped, attempt, null); throw; }
        catch
        { Publish(WorldStreamClientPhase.Faulted, attempt, "unexpected_client_failure"); throw; }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    private void Publish(WorldStreamClientPhase phase, int attempt, string? category, WorldStreamMessage? message = null)
    {
        var state = new WorldStreamClientState(Interlocked.Increment(ref _revision), phase, attempt, category, message,
            _clock.ElapsedMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Volatile.Write(ref _state, state);
    }
    private static TimeSpan Validate(TimeSpan value, string name) => value > TimeSpan.Zero && value <= TimeSpan.FromMinutes(1)
        ? value : throw new ArgumentOutOfRangeException(name);
}
