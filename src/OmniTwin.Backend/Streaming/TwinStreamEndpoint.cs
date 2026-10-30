using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using OmniTwin.Transport;

namespace OmniTwin.Backend.Streaming;

/// <summary>A read-only WebSocket endpoint with a bounded sender and exactly one concurrent receive loop.</summary>
public static class TwinStreamEndpoint
{
    /// <summary>Validates the browser origin/subprotocol, reserves a subscriber slot, and owns the entire connection lifetime.</summary>
    public static async Task HandleAsync(HttpContext context, SnapshotBroker broker, BackendOptions options, IHostApplicationLifetime lifetime)
    {
        if (!AllowedOrigin(context)) { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
        if (!context.WebSockets.IsWebSocketRequest)
        { context.Response.StatusCode = StatusCodes.Status426UpgradeRequired; context.Response.Headers["Upgrade"] = "websocket"; return; }
        var supported = false;
        foreach (var protocol in context.WebSockets.WebSocketRequestedProtocols) if (protocol == WorldStreamCodec.SubProtocol) supported = true;
        if (!supported) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
        WorldStreamSubscription subscription;
        try { subscription = broker.Subscribe(); }
        catch (StreamCapacityException)
        { context.Response.StatusCode = StatusCodes.Status429TooManyRequests; context.Response.Headers["Retry-After"] = "1"; return; }
        catch (InvalidOperationException) { context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable; return; }
        using (subscription)
        using (var socket = await context.WebSockets.AcceptWebSocketAsync(WorldStreamCodec.SubProtocol).ConfigureAwait(false))
            await RunAsync(socket, subscription, broker, options, context.RequestAborted, lifetime.ApplicationStopping).ConfigureAwait(false);
    }

    private static bool AllowedOrigin(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue("Origin", out var origins)) return true;
        return origins.Count == 1 && string.Equals(origins[0], context.Request.Scheme + "://" + context.Request.Host,
            StringComparison.OrdinalIgnoreCase);
    }

    private enum EndReason { PeerClosed, ClientData, BrokerEnded, Cancelled, TransportError }

    internal static async Task RunAsync(WebSocket socket, WorldStreamSubscription subscription, SnapshotBroker broker,
        BackendOptions options, CancellationToken requestAborted, CancellationToken applicationStopping)
    {
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, applicationStopping);
        using var stopSending = CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
        var sender = SendAsync();
        var receiver = ReceiveAsync();
        try
        {
            var first = await Task.WhenAny(sender, receiver).ConfigureAwait(false);
            var reason = await first.ConfigureAwait(false);
            stopSending.Cancel();
            if (first == receiver)
            {
                await sender.WaitAsync(TimeSpan.FromMilliseconds(options.SendTimeoutMilliseconds + 100), connection.Token).ConfigureAwait(false);
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var close = CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
                    close.CancelAfter(TimeSpan.FromSeconds(1));
                    await socket.CloseAsync(reason == EndReason.ClientData ? WebSocketCloseStatus.PolicyViolation : WebSocketCloseStatus.NormalClosure,
                        reason == EndReason.ClientData ? "read_only_stream" : "closed", close.Token).ConfigureAwait(false);
                }
            }
            else if (socket.State == WebSocketState.Open)
            {
                using var close = CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
                close.CancelAfter(TimeSpan.FromSeconds(1));
                await socket.CloseOutputAsync(broker.IsFaulted ? WebSocketCloseStatus.InternalServerError : WebSocketCloseStatus.EndpointUnavailable,
                    broker.IsFaulted ? "perception_failed" : "stream_ended", close.Token).ConfigureAwait(false);
                await receiver.WaitAsync(TimeSpan.FromSeconds(1), connection.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { socket.Abort(); }
        catch (TimeoutException) { socket.Abort(); }
        catch (WebSocketException) { socket.Abort(); }
        finally
        {
            stopSending.Cancel(); connection.Cancel();
            if (socket.State != WebSocketState.Closed) socket.Abort();
            await Task.WhenAll(sender, receiver).ConfigureAwait(false);
        }

        async Task<EndReason> SendAsync()
        {
            try
            {
                await foreach (var frame in subscription.Reader.ReadAllAsync(stopSending.Token).ConfigureAwait(false))
                {
                    if (broker.IsCompleted) return EndReason.BrokerEnded;
                    if (broker.AgeMilliseconds(frame) >= options.MaximumPublicationAgeMilliseconds) continue;
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
                    deadline.CancelAfter(options.SendTimeoutMilliseconds);
                    await socket.SendAsync(frame.Bytes, WebSocketMessageType.Text, true, deadline.Token).ConfigureAwait(false);
                }
                return EndReason.BrokerEnded;
            }
            catch (OperationCanceledException) { return EndReason.Cancelled; }
            catch (WebSocketException) { return EndReason.TransportError; }
        }

        async Task<EndReason> ReceiveAsync()
        {
            try
            {
                var buffer = new byte[1];
                var received = await socket.ReceiveAsync(buffer.AsMemory(), connection.Token).ConfigureAwait(false);
                return received.MessageType == WebSocketMessageType.Close ? EndReason.PeerClosed : EndReason.ClientData;
            }
            catch (OperationCanceledException) { return EndReason.Cancelled; }
            catch (WebSocketException) { return EndReason.TransportError; }
        }
    }
}
