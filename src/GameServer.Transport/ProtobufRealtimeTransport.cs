using GameServer.Protocol;
using GameServer.Protocol.Realtime;
using W = GameServer.Protocol.Realtime.V1;

namespace GameServer.Transport;

/// <summary>
/// Adapts a binary <see cref="IRealtimeChannel"/> of protobuf frames into the
/// kernel's <see cref="IBidirectionalTransport"/>. It enforces the realtime wire
/// rules at the edge — binary-only, version-negotiated, malformed/unknown frames
/// answered with a typed <see cref="W.ServerError"/> — and forwards only the
/// platform messages the kernel understands.
/// </summary>
public sealed class ProtobufRealtimeTransport : IBidirectionalTransport, IInboundActivityProbe
{
    private readonly IRealtimeChannel _channel;
    private readonly RealtimeProtobufCodec _codec;
    private readonly RealtimeEnvelopeMapper _mapper;
    // Every wire frame this adapter reads — including pings it answers and frames it drops, which
    // never reach the server loop. The server reads this to tell an active connection from a zombie.
    private long _inboundFrameCount;

    public ProtobufRealtimeTransport(
        IRealtimeChannel channel,
        RealtimeProtobufCodec codec,
        RealtimeEnvelopeMapper mapper,
        ConnectionId connectionId)
    {
        _channel = channel;
        _codec = codec;
        _mapper = mapper;
        ConnectionId = connectionId;
    }

    public ConnectionId ConnectionId { get; }

    public long InboundFrameCount => Interlocked.Read(ref _inboundFrameCount);

    public async Task SendAsync(MessageEnvelope message, CancellationToken cancellationToken = default)
    {
        var wire = _mapper.MapServer(message);
        await _channel.SendBinaryAsync(_codec.Encode(wire), cancellationToken).ConfigureAwait(false);
    }

    public async Task<MessageEnvelope?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var frame = await _channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);

            if (frame.Kind == RealtimeFrameKind.Closed)
            {
                return null;
            }

            // Any non-close frame is client activity, even one we handle at the edge and never
            // forward (a ping we answer, a frame we drop) — count it before dispatch so the
            // server's liveness check can see a busy connection that produces no kernel messages.
            Interlocked.Increment(ref _inboundFrameCount);

            switch (frame.Kind)
            {
                case RealtimeFrameKind.Text:
                    await SendErrorAsync(
                        W.ErrorCode.TextFrameNotAllowed,
                        "Text frames are not allowed; the realtime protocol is binary protobuf.",
                        W.DisconnectReason.TextFrame, fatal: true, cancellationToken).ConfigureAwait(false);
                    await _channel.CloseAsync(W.DisconnectReason.TextFrame, "text frame not allowed", cancellationToken).ConfigureAwait(false);
                    return null;

                case RealtimeFrameKind.Binary:
                    W.RealtimeEnvelope env;
                    try
                    {
                        env = _codec.Decode(frame.Payload);
                    }
                    catch (RealtimeProtocolException ex)
                    {
                        var fatal = ex.Code == W.ErrorCode.UnsupportedProtocolVersion;
                        await SendErrorAsync(ex.Code, ex.Message, DisconnectReasonFor(ex.Code), fatal, cancellationToken).ConfigureAwait(false);
                        if (fatal)
                        {
                            await _channel.CloseAsync(W.DisconnectReason.UnsupportedVersion, "unsupported protocol version", cancellationToken).ConfigureAwait(false);
                            return null;
                        }

                        continue; // malformed frame: skip it, keep the connection alive
                    }

                    var mapped = _mapper.MapClient(env);
                    switch (mapped.Dispatch)
                    {
                        case RealtimeClientDispatch.ForwardToKernel:
                            return mapped.Kernel;
                        case RealtimeClientDispatch.RespondImmediately:
                            await _channel.SendBinaryAsync(_codec.Encode(mapped.Response!), cancellationToken).ConfigureAwait(false);
                            continue;
                        case RealtimeClientDispatch.Ignore:
                            continue;
                        default:
                            continue;
                    }

                default:
                    return null;
            }
        }
    }

    private async Task SendErrorAsync(W.ErrorCode code, string message, W.DisconnectReason reason, bool fatal, CancellationToken cancellationToken)
    {
        var error = new W.RealtimeEnvelope
        {
            ProtocolVersion = RealtimeProtocol.SupportedVersion,
            MessageType = W.MessageType.ServerError,
            TraceId = "server-error",
            LogicalChannel = W.LogicalChannel.Gameplay,
            ServerError = new W.ServerError
            {
                Code = code,
                Message = message,
                Fatal = fatal,
                DisconnectReason = reason,
            },
        };

        await _channel.SendBinaryAsync(_codec.Encode(error), cancellationToken).ConfigureAwait(false);
    }

    private static W.DisconnectReason DisconnectReasonFor(W.ErrorCode code) => code switch
    {
        W.ErrorCode.UnsupportedProtocolVersion => W.DisconnectReason.UnsupportedVersion,
        _ => W.DisconnectReason.ProtocolViolation,
    };
}
