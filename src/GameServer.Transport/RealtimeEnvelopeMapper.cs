using GameServer.Protocol.Realtime;
using GameServer.Protocol.Realtime.V1;
using K = GameServer.Protocol;

namespace GameServer.Transport;

/// <summary>What the adapter should do with a decoded client message.</summary>
public enum RealtimeClientDispatch
{
    ForwardToKernel,
    RespondImmediately,
    Ignore,
}

/// <summary>The result of mapping an inbound wire message: forward it, answer it directly, or drop it.</summary>
public readonly struct RealtimeClientMessage
{
    private RealtimeClientMessage(RealtimeClientDispatch dispatch, K.MessageEnvelope? kernel, RealtimeEnvelope? response)
    {
        Dispatch = dispatch;
        Kernel = kernel;
        Response = response;
    }

    public RealtimeClientDispatch Dispatch { get; }
    public K.MessageEnvelope? Kernel { get; }
    public RealtimeEnvelope? Response { get; }

    public static RealtimeClientMessage Forward(K.MessageEnvelope kernel) => new(RealtimeClientDispatch.ForwardToKernel, kernel, null);
    public static RealtimeClientMessage Respond(RealtimeEnvelope response) => new(RealtimeClientDispatch.RespondImmediately, null, response);
    public static RealtimeClientMessage Ignored() => new(RealtimeClientDispatch.Ignore, null, null);
}

/// <summary>
/// Translates between the canonical protobuf wire envelope and the kernel's domain
/// <see cref="K.MessageEnvelope"/>. Only platform messages the kernel understands
/// are forwarded; ping is answered with pong, acks/input frames are accepted but
/// not yet simulated, and unknown game payloads fail explicitly with a ServerError.
/// </summary>
public sealed class RealtimeEnvelopeMapper
{
    public RealtimeClientMessage MapClient(RealtimeEnvelope env)
    {
        switch (env.PayloadCase)
        {
            case RealtimeEnvelope.PayloadOneofCase.ClientHello:
                return RealtimeClientMessage.Forward(
                    ToKernel(env, K.MessageType.ClientHello, new K.ClientHello(env.ClientHello.ClientName)));

            case RealtimeEnvelope.PayloadOneofCase.ClientJoinRoom:
                return RealtimeClientMessage.Forward(
                    ToKernel(env, K.MessageType.ClientJoinRoom, new K.ClientJoinRoom(new K.RoomId(env.ClientJoinRoom.RoomId))));

            case RealtimeEnvelope.PayloadOneofCase.ClientLeaveRoom:
                return RealtimeClientMessage.Forward(
                    ToKernel(env, K.MessageType.ClientLeaveRoom, new K.ClientLeaveRoom(new K.RoomId(env.ClientLeaveRoom.RoomId))));

            case RealtimeEnvelope.PayloadOneofCase.ClientCommand:
                // Commands are game-defined strings; the room's game validates them.
                // The platform forwards the raw command unchanged.
                return RealtimeClientMessage.Forward(
                    ToKernel(env, K.MessageType.ClientCommand, new K.ClientCommand(env.ClientCommand.Command)));

            case RealtimeEnvelope.PayloadOneofCase.ClientPing:
                return RealtimeClientMessage.Respond(BuildPong(env));

            case RealtimeEnvelope.PayloadOneofCase.ClientAck:
                // Drives the delta baseline: forward so the server advances what it
                // believes the client holds (see RealtimeServer.HandleAckAsync).
                return RealtimeClientMessage.Forward(ToKernel(env, K.MessageType.ClientAck,
                    new K.ClientAck((long)env.ClientAck.AckedSequence, (long)env.ClientAck.AckedServerTick)));

            case RealtimeEnvelope.PayloadOneofCase.ClientInputFrame:
                // Accepted by the platform but not yet folded into the authoritative sim.
                return RealtimeClientMessage.Ignored();

            case RealtimeEnvelope.PayloadOneofCase.GameMessage:
                return RealtimeClientMessage.Respond(BuildError(
                    env, ErrorCode.UnknownGameMessage,
                    $"No handler for game message '{env.GameMessage.GameMessageType}' (schema v{env.GameMessage.GameSchemaVersion})."));

            default:
                return RealtimeClientMessage.Respond(
                    BuildError(env, ErrorCode.MalformedFrame, "Unsupported or empty realtime payload."));
        }
    }

    public RealtimeEnvelope MapServer(K.MessageEnvelope kernel)
    {
        var wire = new RealtimeEnvelope
        {
            ProtocolVersion = RealtimeProtocol.SupportedVersion,
            MessageId = kernel.TraceId,
            TenantId = kernel.TenantId.Value,
            GameId = kernel.GameId.Value,
            RoomId = kernel.RoomId?.Value ?? string.Empty,
            SessionId = kernel.SessionId?.Value ?? string.Empty,
            PlayerId = kernel.PlayerId?.Value ?? string.Empty,
            Sequence = (ulong)kernel.Sequence,
            TraceId = kernel.TraceId,
            LogicalChannel = LogicalChannel.Gameplay,
        };

        switch (kernel.Payload)
        {
            case K.ServerWelcome welcome:
                wire.MessageType = MessageType.ServerWelcome;
                wire.ServerWelcome = new ServerWelcome
                {
                    AcceptedProtocolVersion = RealtimeProtocol.SupportedVersion,
                    SessionId = welcome.SessionId.Value,
                };
                break;

            case K.ServerSnapshot snapshot:
                wire.MessageType = MessageType.ServerSnapshot;
                wire.ServerTick = (ulong)snapshot.Tick;
                var serverSnapshot = new ServerSnapshot { ServerTick = (ulong)snapshot.Tick };
                foreach (var entity in snapshot.Entities)
                {
                    serverSnapshot.Entities.Add(new EntityState
                    {
                        EntityId = entity.EntityId,
                        Payload = Google.Protobuf.ByteString.CopyFrom(entity.Payload),
                    });
                }

                wire.ServerSnapshot = serverSnapshot;
                break;

            case K.ServerDelta delta:
                wire.MessageType = MessageType.ServerDelta;
                wire.ServerTick = (ulong)delta.ToTick;
                var serverDelta = new ServerDelta
                {
                    FromServerTick = (ulong)delta.FromTick,
                    ToServerTick = (ulong)delta.ToTick,
                };
                foreach (var entity in delta.Changed)
                {
                    serverDelta.ChangedEntities.Add(new EntityState
                    {
                        EntityId = entity.EntityId,
                        Payload = Google.Protobuf.ByteString.CopyFrom(entity.Payload),
                    });
                }

                foreach (var removed in delta.Removed)
                {
                    serverDelta.RemovedEntities.Add(removed);
                }

                wire.ServerDelta = serverDelta;
                break;

            case K.ServerCorrection correction:
                wire.MessageType = MessageType.ServerCorrection;
                wire.ServerTick = (ulong)correction.Tick;
                wire.ServerCorrection = new ServerCorrection
                {
                    ServerTick = (ulong)correction.Tick,
                    AckedClientTick = (ulong)correction.AckedClientTick,
                    AuthoritativeEntity = new EntityState
                    {
                        EntityId = correction.Authoritative.EntityId,
                        Payload = Google.Protobuf.ByteString.CopyFrom(correction.Authoritative.Payload),
                    },
                };
                break;

            case K.ServerEvent serverEvent:
                wire.MessageType = MessageType.ServerEvent;
                wire.ServerEvent = new ServerEvent
                {
                    EventType = serverEvent.EventType,
                    Payload = Google.Protobuf.ByteString.CopyFrom(serverEvent.Payload),
                };
                break;

            case K.ServerError error:
                wire.MessageType = MessageType.ServerError;
                wire.ServerError = new ServerError { Code = MapErrorCode(error.Code), Message = error.Message };
                break;

            default:
                throw new InvalidOperationException($"Cannot map kernel payload '{kernel.Payload.Type}' to the wire protocol.");
        }

        return wire;
    }

    private static K.MessageEnvelope ToKernel(RealtimeEnvelope env, K.MessageType type, K.IMessagePayload payload) => new()
    {
        TenantId = new K.TenantId(env.TenantId),
        GameId = new K.GameId(env.GameId),
        RoomId = string.IsNullOrEmpty(env.RoomId) ? null : new K.RoomId(env.RoomId),
        SessionId = string.IsNullOrEmpty(env.SessionId) ? null : new K.SessionId(env.SessionId),
        PlayerId = string.IsNullOrEmpty(env.PlayerId) ? null : new K.PlayerId(env.PlayerId),
        ProtocolVersion = (int)env.ProtocolVersion,
        MessageType = type,
        Sequence = (long)env.Sequence,
        TraceId = string.IsNullOrEmpty(env.TraceId) ? env.MessageId : env.TraceId,
        Payload = payload,
    };

    private static RealtimeEnvelope BuildError(RealtimeEnvelope source, ErrorCode code, string message)
    {
        var wire = CloneCorrelation(source, MessageType.ServerError);
        wire.ServerError = new ServerError { Code = code, Message = message };
        return wire;
    }

    private static RealtimeEnvelope BuildPong(RealtimeEnvelope source)
    {
        var wire = CloneCorrelation(source, MessageType.ServerPong);
        wire.ServerPong = new ServerPong
        {
            ClientTick = source.ClientTick,
            Nonce = source.ClientPing?.Nonce ?? string.Empty,
        };
        return wire;
    }

    private static RealtimeEnvelope CloneCorrelation(RealtimeEnvelope source, MessageType type) => new()
    {
        ProtocolVersion = RealtimeProtocol.SupportedVersion,
        MessageId = source.MessageId,
        MessageType = type,
        TenantId = source.TenantId,
        GameId = source.GameId,
        RoomId = source.RoomId,
        SessionId = source.SessionId,
        PlayerId = source.PlayerId,
        ConnectionId = source.ConnectionId,
        Sequence = source.Sequence,
        Ack = source.Ack,
        ClientTick = source.ClientTick,
        TraceId = source.TraceId,
        LogicalChannel = source.LogicalChannel,
    };

    private static ErrorCode MapErrorCode(K.ServerErrorCode code) => code switch
    {
        K.ServerErrorCode.UnknownTenant => ErrorCode.TenantNotFound,
        K.ServerErrorCode.UnsupportedProtocolVersion => ErrorCode.UnsupportedProtocolVersion,
        K.ServerErrorCode.InvalidCommand => ErrorCode.MalformedFrame,
        K.ServerErrorCode.StaleSequence => ErrorCode.SequenceRejected,
        K.ServerErrorCode.NotJoined => ErrorCode.PlayerNotInRoom,
        K.ServerErrorCode.RoomUnavailable => ErrorCode.RoomNotFound,
        K.ServerErrorCode.MalformedMessage => ErrorCode.MalformedFrame,
        // An authorization rejection (cross-tenant / scope-escalation handshake) must surface as
        // UNAUTHORIZED on the wire, not a misleading INTERNAL_SERVER_ERROR — the decision is correct
        // (access denied), but the client/log needs the honest code. (Finding 5.)
        K.ServerErrorCode.Unauthorized => ErrorCode.Unauthorized,
        // Load-shedding under backpressure (full room queue / admission cap) is a retryable
        // BACKPRESSURE_REJECTED, not an internal fault.
        K.ServerErrorCode.Overloaded => ErrorCode.BackpressureRejected,
        _ => ErrorCode.InternalServerError,
    };
}
