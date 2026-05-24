using GameServer.Protocol;

namespace GameServer.Transport;

/// <summary>
/// A bidirectional text-frame channel (e.g. a WebSocket). This is the seam that
/// keeps the protocol/codec adaptation testable without a real socket: production
/// wraps a <c>System.Net.WebSockets.WebSocket</c>; tests use an in-memory fake.
/// </summary>
public interface IWebSocketChannel
{
    /// <summary>Returns the next inbound text frame, or <c>null</c> when the channel is closed.</summary>
    Task<string?> ReceiveTextAsync(CancellationToken cancellationToken);

    /// <summary>Sends one outbound text frame. Throws if the channel is no longer writable.</summary>
    Task SendTextAsync(string text, CancellationToken cancellationToken);
}

/// <summary>
/// Adapts an <see cref="IWebSocketChannel"/> of text frames into the kernel's
/// <see cref="IBidirectionalTransport"/> of protocol envelopes, using an
/// <see cref="IMessageCodec"/>. Inbound frames that cannot be decoded never reach
/// the server: the adapter answers with a <see cref="ServerError"/> and moves on,
/// so one malformed frame cannot corrupt the session.
/// </summary>
public sealed class WebSocketChannelTransport : IBidirectionalTransport
{
    private readonly IWebSocketChannel _channel;
    private readonly IMessageCodec _codec;
    private long _errorSequence;

    public WebSocketChannelTransport(IWebSocketChannel channel, IMessageCodec codec, ConnectionId connectionId)
    {
        _channel = channel;
        _codec = codec;
        ConnectionId = connectionId;
    }

    public ConnectionId ConnectionId { get; }

    public async Task SendAsync(MessageEnvelope message, CancellationToken cancellationToken = default)
    {
        var wire = _codec.Encode(message);
        await _channel.SendTextAsync(wire, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MessageEnvelope?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        while (await _channel.ReceiveTextAsync(cancellationToken).ConfigureAwait(false) is { } text)
        {
            try
            {
                return _codec.Decode(text);
            }
            catch (MessageCodecException ex)
            {
                await SendMalformedErrorAsync(ex.Message, cancellationToken).ConfigureAwait(false);
                // Skip the bad frame and keep reading; the connection survives.
            }
        }

        return null; // channel closed
    }

    private async Task SendMalformedErrorAsync(string detail, CancellationToken cancellationToken)
    {
        var envelope = new MessageEnvelope
        {
            TenantId = new TenantId("unknown"),
            GameId = new GameId("unknown"),
            ProtocolVersion = ProtocolVersions.Current,
            MessageType = MessageType.ServerError,
            Sequence = Interlocked.Increment(ref _errorSequence),
            TraceId = "malformed",
            Payload = new ServerError(ServerErrorCode.MalformedMessage, $"Malformed message: {detail}"),
        };

        await _channel.SendTextAsync(_codec.Encode(envelope), cancellationToken).ConfigureAwait(false);
    }
}
