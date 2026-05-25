using GameServer.Protocol.Realtime;
using GameServer.Protocol.Realtime.V1;

namespace GameServer.SecurityTests;

/// <summary>
/// Hand-builds canonical wire <see cref="RealtimeEnvelope"/>s for black-box probing. Unlike the
/// kernel's mapper these set the envelope identity fields (tenant/game/room/player) to whatever a
/// test wants, so a connection authorized for one scope can attempt to ACT in another — which is
/// exactly what the server must reject (<see cref="RealtimeServer"/> trusts the verified token,
/// never the declared envelope identity).
/// </summary>
public static class WireEnvelopes
{
    private static RealtimeEnvelope Base(MessageType type, string traceId) => new()
    {
        ProtocolVersion = RealtimeProtocol.SupportedVersion,
        MessageId = Guid.NewGuid().ToString("n"),
        MessageType = type,
        TraceId = traceId,
        LogicalChannel = LogicalChannel.Gameplay,
    };

    public static RealtimeEnvelope Hello(string tenantId, string gameId, string clientName = "sec-probe")
    {
        var env = Base(MessageType.ClientHello, "hello");
        env.TenantId = tenantId;
        env.GameId = gameId;
        env.ClientHello = new ClientHello
        {
            RequestedProtocolVersion = RealtimeProtocol.SupportedVersion,
            ClientName = clientName,
        };
        return env;
    }

    /// <summary>A ClientHello declaring an UNSUPPORTED protocol version at the envelope level.</summary>
    public static RealtimeEnvelope HelloWithUnsupportedVersion(string tenantId, string gameId)
    {
        var env = Hello(tenantId, gameId);
        // Force an unsupported envelope version; the codec rejects this before the kernel sees it.
        env.ProtocolVersion = ProtocolVersion.Unspecified;
        return env;
    }

    public static RealtimeEnvelope JoinRoom(string tenantId, string gameId, string roomId, string playerId)
    {
        var env = Base(MessageType.ClientJoinRoom, "join");
        env.TenantId = tenantId;
        env.GameId = gameId;
        env.RoomId = roomId;
        env.PlayerId = playerId;
        env.ClientJoinRoom = new ClientJoinRoom { RoomId = roomId };
        return env;
    }

    public static RealtimeEnvelope Command(string tenantId, string gameId, string roomId, string playerId, string command, ulong sequence)
    {
        var env = Base(MessageType.ClientCommand, "cmd");
        env.TenantId = tenantId;
        env.GameId = gameId;
        env.RoomId = roomId;
        env.PlayerId = playerId;
        env.Sequence = sequence;
        env.ClientCommand = new ClientCommand { Command = command };
        return env;
    }
}
