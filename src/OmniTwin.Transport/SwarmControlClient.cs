using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using OmniTwin.Contracts;

namespace OmniTwin.Transport;

/// <summary>An atomic read view of current controls; transport success does not imply perception trust.</summary>
public readonly struct SwarmControlClientView
{
    internal SwarmControlClientView(SwarmControlSnapshot? control, bool fresh, string? failure)
    { Control = control; HasFreshReply = fresh; FailureCategory = failure; }
    /// <summary>Gets the last validated reply.</summary>
    public SwarmControlSnapshot? Control { get; }
    /// <summary>Gets whether that reply was received within 750 ms and transport remains healthy.</summary>
    public bool HasFreshReply { get; }
    /// <summary>Gets a safe network/protocol category.</summary>
    public string? FailureCategory { get; }
}

/// <summary>Runs bounded telemetry and mission I/O on independent async loops, with latest-only handoffs.</summary>
public sealed class SwarmControlClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new object();
    private readonly SemaphoreSlim _missionReady = new SemaphoreSlim(0, 1);
    private SwarmTelemetrySnapshot? _telemetry;
    private SwarmMissionRequest? _mission;
    private SwarmControlSnapshot? _control;
    private string? _failure, _missionFailure;
    private long _received = -1;
    private int _running, _disposed;

    /// <summary>Creates a nonredirecting, unauthenticated local transport; provider keys never enter this client.</summary>
    public SwarmControlClient()
    {
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }
    /// <summary>Publishes owned telemetry; pending older samples are replaced.</summary>
    public void PublishTelemetry(SwarmTelemetrySnapshot telemetry)
    {
        if (telemetry is null) throw new ArgumentNullException(nameof(telemetry));
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(SwarmControlClient));
        Interlocked.Exchange(ref _telemetry, telemetry);
    }
    /// <summary>Queues the newest explicit operator command without blocking the caller.</summary>
    public void QueueMission(SwarmMissionRequest mission)
    {
        if (mission is null) throw new ArgumentNullException(nameof(mission));
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(SwarmControlClient));
        lock (_gate) _missionFailure = null;
        if (Interlocked.Exchange(ref _mission, mission) is null) _missionReady.Release();
    }
    /// <summary>Returns an immutable view with freshness checked at read time.</summary>
    public SwarmControlClientView GetView()
    { lock (_gate) return new SwarmControlClientView(_control, _failure is null && _control is not null && _clock.ElapsedMilliseconds - _received < 750, _failure ?? _missionFailure); }

    /// <summary>Starts one lifetime of local HTTP I/O; cancellation stops both loops and invalidates controls.</summary>
    public async Task RunAsync(Uri origin, CancellationToken cancellationToken)
    {
        if (origin is null || origin.Scheme != "http" || !(origin.Host == "127.0.0.1" || origin.Host == "localhost") ||
            origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.UserInfo.Length != 0)
            throw new ArgumentException("Use a loopback HTTP origin.", nameof(origin));
        if (Interlocked.Exchange(ref _running, 1) != 0) throw new InvalidOperationException("A control loop is already running.");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var telemetryLoop = TelemetryLoop(); var missionLoop = MissionLoop();
        try
        {
            await Task.WhenAny(telemetryLoop, missionLoop).ConfigureAwait(false);
            stop.Cancel();
            await Task.WhenAll(telemetryLoop, missionLoop).ConfigureAwait(false);
        }
        finally { stop.Cancel(); lock (_gate) { _control = null; _received = -1; } Volatile.Write(ref _running, 0); }

        async Task TelemetryLoop()
        {
            long sequence = -1; Guid session = Guid.Empty;
            while (true)
            {
                stop.Token.ThrowIfCancellationRequested();
                var telemetry = Volatile.Read(ref _telemetry);
                if (telemetry is not null && (telemetry.SessionId != session || telemetry.SequenceNumber > sequence))
                {
                    try
                    {
                        var bytes = await PostAsync(new Uri(origin, "api/swarm/telemetry"), SwarmWireCodec.EncodeTelemetry(telemetry), stop.Token,
                            retrySessionConflict: true).ConfigureAwait(false);
                        var control = SwarmWireCodec.DecodeControl(bytes);
                        var delivery = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - control.TimestampUnixMs;
                        if (control.TelemetrySessionId != telemetry.SessionId || control.TelemetrySequenceNumber != telemetry.SequenceNumber ||
                            delivery < -500 || delivery > 2000 || control.Directives.Count != telemetry.Agents.Count)
                            throw new WorldStreamProtocolException("control_correlation_or_age");
                        var ids = new System.Collections.Generic.HashSet<int>(); foreach (var agent in telemetry.Agents) ids.Add(agent.AgentId);
                        foreach (var directive in control.Directives) if (!ids.Remove(directive.AgentId)) throw new WorldStreamProtocolException("control_recipient");
                        lock (_gate) { _control = control; _received = _clock.ElapsedMilliseconds; _failure = null; }
                        sequence = telemetry.SequenceNumber; session = telemetry.SessionId;
                    }
                    catch (WorldStreamProtocolException error) { Fail(error.Category); }
                    catch (TransportFailure error) { Fail(error.Message); }
                    catch (IOException) { Fail("control_network_error"); }
                }
                await Task.Delay(50, stop.Token).ConfigureAwait(false);
            }
        }
        async Task MissionLoop()
        {
            while (true)
            {
                await _missionReady.WaitAsync(stop.Token).ConfigureAwait(false);
                var mission = Interlocked.Exchange(ref _mission, null); if (mission is null) continue;
                try
                {
                    var bytes = await PostAsync(new Uri(origin, "api/swarm/mission"), SwarmWireCodec.EncodeMission(mission), stop.Token).ConfigureAwait(false);
                    using var document = System.Text.Json.JsonDocument.Parse(bytes);
                    var root = document.RootElement; WorldStreamCodec.Keys(root, "accepted", "commandId");
                    if (!WorldStreamCodec.Boolean(root, "accepted") || WorldStreamCodec.Id(root, "commandId") != mission.Command.CommandId)
                        throw new WorldStreamProtocolException("mission_correlation");
                    lock (_gate) _missionFailure = null;
                }
                catch (WorldStreamProtocolException error) { MissionFail(error.Category); }
                catch (System.Text.Json.JsonException) { MissionFail("invalid_mission_reply"); }
                catch (TransportFailure error) { MissionFail(error.Message); }
                catch (IOException) { MissionFail("control_network_error"); }
            }
        }
    }

    private void Fail(string category) { lock (_gate) { _failure = category; _control = null; _received = -1; } }
    private void MissionFail(string category) { lock (_gate) _missionFailure = category; }
    private async Task<byte[]> PostAsync(Uri uri, byte[] bytes, CancellationToken cancellationToken, bool retrySessionConflict = false)
    {
        var started = _clock.ElapsedMilliseconds;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var remaining = 5000 - (_clock.ElapsedMilliseconds - started); if (remaining <= 0) break;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(1500, remaining)));
            var delay = TimeSpan.FromMilliseconds(100 * (1 << attempt));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, uri);
                request.Headers.Add("X-OmniTwin-Swarm", "1"); request.Content = new ByteArrayContent(bytes);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    if (response.Content.Headers.ContentType?.MediaType != "application/json" || response.Content.Headers.ContentLength > SwarmWireCodec.MaximumBytes)
                        throw new WorldStreamProtocolException("control_content_type_or_size");
                    using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    using var output = new MemoryStream(); var buffer = new byte[4096];
                    while (true)
                    { var count = await stream.ReadAsync(buffer, 0, buffer.Length, deadline.Token).ConfigureAwait(false); if (count == 0) break;
                        if (output.Length + count > SwarmWireCodec.MaximumBytes) throw new WorldStreamProtocolException("control_body_size"); output.Write(buffer, 0, count); }
                    return output.ToArray();
                }
                var retryableSessionConflict = status == 409 && retrySessionConflict;
                if (retryableSessionConflict && attempt == 2) throw new TransportFailure("control_session_in_use");
                if (!retryableSessionConflict && !(status == 408 || status == 429 || status == 500 || status == 502 || status == 503 || status == 504))
                    throw new TransportFailure("control_http_" + status);
                var advised = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow);
                if (advised.HasValue && advised.Value > delay) delay = advised.Value;
            }
            catch (HttpRequestException) { if (attempt == 2) throw new TransportFailure("control_network_error"); }
            catch (TransportFailure) { throw; }
            catch (IOException) { if (attempt == 2) throw new TransportFailure("control_network_error"); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { if (attempt == 2) throw new TransportFailure("control_timeout"); }
            if (attempt == 2 || delay.TotalMilliseconds >= 5000 - (_clock.ElapsedMilliseconds - started)) break;
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        throw new TransportFailure("control_retry_budget");
    }
    /// <summary>Disposes the client after its owner has cancelled and awaited RunAsync.</summary>
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) { _http.Dispose(); _missionReady.Dispose(); } }
    private sealed class TransportFailure : IOException
    { internal TransportFailure(string category) : base(category) { } }
}
