using System;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using OmniTwin.Contracts;
using OmniTwin.Transport;

internal static class StreamProbe
{
    internal static async Task RunAsync(Uri endpoint, int messageCount, Action<WorldStreamMessage> emit, CancellationToken token)
    {
        if (!endpoint.IsAbsoluteUri || (endpoint.Scheme != "ws" && endpoint.Scheme != "wss") || endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0)
            throw new ArgumentException("Provide an absolute WebSocket URL without credentials or a fragment.", nameof(endpoint));
        if (messageCount < 1 || messageCount > 300) throw new ArgumentOutOfRangeException(nameof(messageCount));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var socket = await ConnectAsync(endpoint, deadline.Token).ConfigureAwait(false);
            try
            {
                var count = 0;
                await foreach (var message in new WorldStreamReceiver().ReadAllAsync(socket, deadline.Token).ConfigureAwait(false))
                {
                    emit(message);
                    if (++count == messageCount)
                    {
                        using var close = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                        close.CancelAfter(TimeSpan.FromSeconds(1));
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "probe_completed", close.Token).ConfigureAwait(false);
                        return;
                    }
                }
                throw new WorldStreamConnectionException("stream_ended_before_requested_count");
            }
            finally { if (socket.State != WebSocketState.Closed) socket.Abort(); }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new WorldStreamConnectionException("probe_deadline"); }
        catch (WebSocketException) { throw new WorldStreamConnectionException("close_handshake_failed"); }
    }

    private static async Task<ClientWebSocket> ConnectAsync(Uri endpoint, CancellationToken token)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var socket = new ClientWebSocket();
            socket.Options.AddSubProtocol(WorldStreamCodec.SubProtocol);
            socket.Options.CollectHttpResponseDetails = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var delay = TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt));
            try
            {
                await socket.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
                return socket;
            }
            catch (WebSocketException)
            {
                var status = socket.HttpStatusCode;
                var permanent = (int)status >= 300 && status is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or
                    HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout);
                if (socket.HttpResponseHeaders is not null && socket.HttpResponseHeaders.TryGetValue("Retry-After", out var values))
                    foreach (var value in values)
                    {
                        TimeSpan? advised = null;
                        if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
                        {
                            if (seconds > 30) { socket.Dispose(); throw new WorldStreamConnectionException("retry_after_exceeds_probe_deadline"); }
                            advised = TimeSpan.FromSeconds(seconds);
                        }
                        else if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
                            advised = date - DateTimeOffset.UtcNow;
                        if (advised > TimeSpan.FromSeconds(30))
                        { socket.Dispose(); throw new WorldStreamConnectionException("retry_after_exceeds_probe_deadline"); }
                        if (advised.HasValue && advised.Value > delay) delay = advised.Value;
                    }
                socket.Dispose();
                if (permanent || attempt == 2) throw new WorldStreamConnectionException("connect_rejected_or_unavailable");
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { socket.Dispose(); if (attempt == 2) throw new WorldStreamConnectionException("connect_timeout"); }
            catch { socket.Dispose(); throw; }
            await Task.Delay(delay, token).ConfigureAwait(false);
        }
        throw new WorldStreamConnectionException("connect_attempts_exhausted");
    }
}
