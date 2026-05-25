using System.Net.WebSockets;
using GameServer.Protocol.Realtime.V1;
using GameServer.Transport;

namespace GameServer.Host;

/// <summary>
/// Wraps an ASP.NET Core <see cref="WebSocket"/> as a binary <see cref="IRealtimeChannel"/>.
/// Surfaces inbound frame kind (so the adapter can reject text frames), serializes
/// binary sends, and closes with a stable reason. A closed/aborted socket surfaces
/// as a <see cref="RealtimeFrameKind.Closed"/> frame.
/// </summary>
public sealed class AspNetRealtimeChannel : IRealtimeChannel
{
    /// <summary>Default cap on a single reassembled inbound message. A realtime command frame is
    /// small; this bounds the memory one connection can force the server to buffer.</summary>
    public const int DefaultMaxMessageBytes = 64 * 1024;

    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly byte[] _receiveBuffer = new byte[8 * 1024];
    private readonly int _maxMessageBytes;

    public AspNetRealtimeChannel(WebSocket socket, int maxMessageBytes = DefaultMaxMessageBytes)
    {
        if (maxMessageBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMessageBytes), maxMessageBytes, "Max message size must be positive.");
        }

        _socket = socket;
        _maxMessageBytes = maxMessageBytes;
    }

    public async Task<RealtimeInboundFrame> ReceiveAsync(CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        WebSocketReceiveResult result = null!;

        try
        {
            do
            {
                result = await _socket.ReceiveAsync(new ArraySegment<byte>(_receiveBuffer), cancellationToken)
                    .ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return RealtimeInboundFrame.Closed;
                }

                // Bound reassembly BEFORE buffering: a client that streams an unbounded message
                // (never setting EndOfMessage) would otherwise grow this MemoryStream until the
                // process is OOM-killed. Once the cap is exceeded the message can never be valid,
                // so stop reading and drop the connection (observable as a closed frame).
                if (message.Length + result.Count > _maxMessageBytes)
                {
                    return RealtimeInboundFrame.Closed;
                }

                message.Write(_receiveBuffer, 0, result.Count);
            }
            while (!result.EndOfMessage);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            return RealtimeInboundFrame.Closed;
        }

        var kind = result.MessageType == WebSocketMessageType.Text
            ? RealtimeFrameKind.Text
            : RealtimeFrameKind.Binary;
        return new RealtimeInboundFrame(kind, message.ToArray());
    }

    public async Task SendBinaryAsync(byte[] payload, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(
                new ArraySegment<byte>(payload), WebSocketMessageType.Binary, endOfMessage: true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task CloseAsync(DisconnectReason reason, string description, CancellationToken cancellationToken)
    {
        if (_socket.State != WebSocketState.Open)
        {
            return;
        }

        try
        {
            await _socket.CloseAsync(WebSocketCloseStatus.ProtocolError, $"{reason}: {description}", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // The peer may have already gone away; closing is best-effort.
        }
    }
}
