using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using OmniTwin.Contracts;

namespace OmniTwin.Transport;

/// <summary>A bounded stream connection failure without including peer-controlled reason text.</summary>
public sealed class WorldStreamConnectionException : IOException
{
    /// <summary>Creates a connection failure with a stable category.</summary>
    public WorldStreamConnectionException(string category) : base("World stream connection failure: " + category) => Category = category;
    /// <summary>Gets the failure category.</summary>
    public string Category { get; }
}

/// <summary>Reassembles bounded text messages, validates wire data and order, and permits one active reader.</summary>
public sealed class WorldStreamReceiver
{
    private readonly TimeSpan _messageTimeout;
    private readonly int _maximumFragments;
    private int _reading;

    /// <summary>Sets the whole-message deadline, including idle wait, and the fragmentation limit.</summary>
    public WorldStreamReceiver(TimeSpan? messageTimeout = null, int maximumFragments = 128)
    {
        _messageTimeout = messageTimeout ?? TimeSpan.FromSeconds(10);
        if (_messageTimeout <= TimeSpan.Zero || _messageTimeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(messageTimeout));
        if (maximumFragments < 1 || maximumFragments > 1024) throw new ArgumentOutOfRangeException(nameof(maximumFragments));
        _maximumFragments = maximumFragments;
    }

    /// <summary>Reads one negotiated connection. Caller cancellation propagates; a new connection needs a new enumeration.</summary>
    /// <remarks>On completion or failure, the consumer must mark its presentation state disconnected.</remarks>
    public async IAsyncEnumerable<WorldStreamMessage> ReadAllAsync(WebSocket socket,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (socket is null) throw new ArgumentNullException(nameof(socket));
        if (socket.SubProtocol != WorldStreamCodec.SubProtocol) throw new WorldStreamProtocolException("subprotocol_required");
        if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0) throw new InvalidOperationException("Only one receive loop is allowed.");
        try
        {
            var order = new WorldStreamOrderGuard();
            while (true)
            {
                var bytes = await ReadMessageAsync(socket, cancellationToken).ConfigureAwait(false);
                if (bytes is null) yield break;
                var message = WorldStreamCodec.Decode(bytes);
                order.Accept(message);
                yield return message;
            }
        }
        finally { Interlocked.Exchange(ref _reading, 0); }
    }

    private async Task<byte[]?> ReadMessageAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_messageTimeout);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        try
        {
            for (var fragments = 0; fragments < _maximumFragments; fragments++)
            {
                var received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    if (bytes.Length != 0) throw new WorldStreamProtocolException("incomplete_message");
                    if (socket.State == WebSocketState.CloseReceived)
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ack", deadline.Token).ConfigureAwait(false);
                    if (received.CloseStatus is not null && received.CloseStatus != WebSocketCloseStatus.NormalClosure &&
                        received.CloseStatus != WebSocketCloseStatus.EndpointUnavailable)
                        throw new WorldStreamConnectionException("server_closed_with_error");
                    return null;
                }
                if (received.MessageType != WebSocketMessageType.Text) throw new WorldStreamProtocolException("text_message_required");
                if (bytes.Length + received.Count > WorldStreamCodec.MaximumMessageBytes) throw new WorldStreamProtocolException("message_too_large");
                bytes.Write(buffer, 0, received.Count);
                if (received.EndOfMessage) return bytes.ToArray();
            }
            throw new WorldStreamProtocolException("fragment_limit");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { socket.Abort(); throw new WorldStreamConnectionException("receive_timeout"); }
        catch (WebSocketException) { throw new WorldStreamConnectionException("socket_error"); }
    }
}
