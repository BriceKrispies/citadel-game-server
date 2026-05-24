using GameServer.Protocol.Realtime;
using GameServer.Protocol.Realtime.V1;

namespace GameServer.LoadHarness;

/// <summary>
/// Builds outbound client messages and parses inbound server messages using the
/// canonical binary protobuf codec. This is the only place the harness touches the
/// wire schema, so the protocol contract is exercised exactly as a real client would.
/// </summary>
public sealed class ProtocolClient
{
    private readonly RealtimeProtobufCodec _codec = new();
    private readonly string _tenantId;
    private readonly string _gameId;
    private readonly string _playerId;

    public ProtocolClient(string tenantId, string gameId, string playerId)
    {
        _tenantId = tenantId;
        _gameId = gameId;
        _playerId = playerId;
    }

    public byte[] Hello(string joinToken, long sequence)
    {
        var env = Base(MessageType.ClientHello, room: null, sequence);
        env.ClientHello = new ClientHello
        {
            RequestedProtocolVersion = ProtocolVersion.V1,
            JoinToken = joinToken,
            ClientName = _playerId,
            SupportedProtocolVersions = { ProtocolVersion.V1 },
        };
        return _codec.Encode(env);
    }

    public byte[] JoinRoom(string room, long sequence)
    {
        var env = Base(MessageType.ClientJoinRoom, room, sequence);
        env.ClientJoinRoom = new ClientJoinRoom { RoomId = room };
        return _codec.Encode(env);
    }

    public byte[] InputFrame(string room, long clientTick, long sequence, string command = "MoveRight")
    {
        var env = Base(MessageType.ClientInputFrame, room, sequence);
        env.ClientTick = (ulong)clientTick;
        var frame = new ClientInputFrame { ClientTick = (ulong)clientTick };
        frame.Commands.Add(new InputCommand { Command = command });
        env.ClientInputFrame = frame;
        return _codec.Encode(env);
    }

    public byte[] Ack(string room, ulong ackedSequence, ulong ackedServerTick, long sequence)
    {
        var env = Base(MessageType.ClientAck, room, sequence);
        env.Ack = ackedSequence;
        env.ServerTick = ackedServerTick;
        env.ClientAck = new ClientAck { AckedSequence = ackedSequence, AckedServerTick = ackedServerTick };
        return _codec.Encode(env);
    }

    public byte[] Ping(string room, long clientTick, long sequence)
    {
        var env = Base(MessageType.ClientPing, room, sequence);
        env.ClientTick = (ulong)clientTick;
        env.ClientPing = new ClientPing { ClientTick = (ulong)clientTick, Nonce = $"{_playerId}-{sequence}" };
        return _codec.Encode(env);
    }

    /// <summary>Deliberately invalid protobuf (over-long varint) for the malformed-clients scenario.</summary>
    public byte[] MalformedFrame() => new byte[] { 0x08, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F };

    public RealtimeEnvelope Parse(byte[] frame) => _codec.Decode(frame);

    private RealtimeEnvelope Base(MessageType type, string? room, long sequence) => new()
    {
        ProtocolVersion = ProtocolVersion.V1,
        MessageId = $"{_playerId}-{sequence}",
        MessageType = type,
        TenantId = _tenantId,
        GameId = _gameId,
        RoomId = room ?? string.Empty,
        PlayerId = _playerId,
        Sequence = (ulong)sequence,
        TraceId = $"{_playerId}-{sequence}",
        LogicalChannel = LogicalChannel.Gameplay,
    };
}
