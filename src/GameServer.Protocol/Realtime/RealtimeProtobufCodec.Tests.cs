using Xunit;
using V1 = GameServer.Protocol.Realtime.V1;

namespace GameServer.Protocol.Realtime;

/// <summary>
/// Contract tests for the canonical binary protocol codec: round-trips, malformed
/// rejection, version rejection, and envelope field preservation. Wire types are
/// referenced via the <c>V1</c> alias to avoid clashing with the kernel's own
/// same-named domain types.
/// </summary>
public sealed class RealtimeProtobufCodecTests
{
    private readonly RealtimeProtobufCodec _codec = new();

    private static V1.RealtimeEnvelope BaseEnvelope(V1.MessageType type) => new()
    {
        ProtocolVersion = V1.ProtocolVersion.V1,
        MessageId = "msg-1",
        MessageType = type,
        TenantId = "tenant-a",
        GameId = "demo-game",
        RoomId = "room-1",
        SessionId = "session-1",
        PlayerId = "player-1",
        ConnectionId = "conn-1",
        Sequence = 7,
        Ack = 3,
        ClientTick = 11,
        ServerTick = 99,
        TraceId = "trace-1",
        LogicalChannel = V1.LogicalChannel.Gameplay,
    };

    [Fact]
    public void ProtobufCodec_RoundTripsClientHello()
    {
        var env = BaseEnvelope(V1.MessageType.ClientHello);
        env.ClientHello = new V1.ClientHello
        {
            RequestedProtocolVersion = V1.ProtocolVersion.V1,
            JoinToken = "join-token-1",
            ClientName = "Alice",
        };

        var decoded = _codec.Decode(_codec.Encode(env));

        Assert.Equal(env, decoded);
        Assert.Equal("Alice", decoded.ClientHello.ClientName);
        Assert.Equal(V1.RealtimeEnvelope.PayloadOneofCase.ClientHello, decoded.PayloadCase);
    }

    [Fact]
    public void ProtobufCodec_RoundTripsServerWelcome()
    {
        var env = BaseEnvelope(V1.MessageType.ServerWelcome);
        env.ServerWelcome = new V1.ServerWelcome
        {
            AcceptedProtocolVersion = V1.ProtocolVersion.V1,
            SessionId = "session-1",
            ConnectionId = "conn-1",
            ServerTick = 5,
        };

        var decoded = _codec.Decode(_codec.Encode(env));

        Assert.Equal(env, decoded);
        Assert.Equal(V1.ProtocolVersion.V1, decoded.ServerWelcome.AcceptedProtocolVersion);
    }

    [Fact]
    public void ProtobufCodec_RoundTripsClientInputFrame()
    {
        var env = BaseEnvelope(V1.MessageType.ClientInputFrame);
        var frame = new V1.ClientInputFrame { ClientTick = 42 };
        frame.Commands.Add(new V1.InputCommand { Command = "MoveRight" });
        env.ClientInputFrame = frame;

        var decoded = _codec.Decode(_codec.Encode(env));

        Assert.Equal(env, decoded);
        Assert.Equal(42UL, decoded.ClientInputFrame.ClientTick);
        Assert.Equal("MoveRight", Assert.Single(decoded.ClientInputFrame.Commands).Command);
    }

    [Fact]
    public void ProtobufCodec_RejectsMalformedBinaryFrame()
    {
        // An over-long varint for field 1: invalid protobuf wire format.
        var malformed = new byte[] { 0x08, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F };

        var ex = Assert.Throws<RealtimeProtocolException>(() => _codec.Decode(malformed));
        Assert.Equal(V1.ErrorCode.MalformedFrame, ex.Code);
    }

    [Fact]
    public void ProtobufCodec_RejectsUnsupportedProtocolVersion()
    {
        var env = BaseEnvelope(V1.MessageType.ClientHello);
        env.ProtocolVersion = (V1.ProtocolVersion)999; // a version this build does not speak
        env.ClientHello = new V1.ClientHello { ClientName = "x" };
        var bytes = _codec.Encode(env);

        var ex = Assert.Throws<RealtimeProtocolException>(() => _codec.Decode(bytes));
        Assert.Equal(V1.ErrorCode.UnsupportedProtocolVersion, ex.Code);
    }

    [Fact]
    public void RealtimeEnvelope_RequiresTenantGameRoomSessionPlayerTraceFields()
    {
        var env = BaseEnvelope(V1.MessageType.ClientCommand);
        env.ClientCommand = new V1.ClientCommand { Command = "MoveRight" };

        var decoded = _codec.Decode(_codec.Encode(env));

        Assert.Equal("tenant-a", decoded.TenantId);
        Assert.Equal("demo-game", decoded.GameId);
        Assert.Equal("room-1", decoded.RoomId);
        Assert.Equal("session-1", decoded.SessionId);
        Assert.Equal("player-1", decoded.PlayerId);
        Assert.Equal("conn-1", decoded.ConnectionId);
        Assert.Equal("trace-1", decoded.TraceId);
    }

    [Fact]
    public void RealtimeEnvelope_PreservesSequenceAckAndTicks()
    {
        var env = BaseEnvelope(V1.MessageType.ClientAck);
        env.ClientAck = new V1.ClientAck { AckedSequence = 7, AckedServerTick = 99 };

        var decoded = _codec.Decode(_codec.Encode(env));

        Assert.Equal(7UL, decoded.Sequence);
        Assert.Equal(3UL, decoded.Ack);
        Assert.Equal(11UL, decoded.ClientTick);
        Assert.Equal(99UL, decoded.ServerTick);
    }
}
