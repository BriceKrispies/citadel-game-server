using GameServer.Identity;
using GameServer.Protocol;
using GameServer.Protocol.Realtime;
using GameServer.Transport.Testing;
using Google.Protobuf;
using Xunit;
using Wire = GameServer.Protocol.Realtime.V1;

namespace GameServer.Transport;

/// <summary>
/// Behavioral tests for the binary realtime endpoint adapter, driven in-process via
/// a <see cref="FakeRealtimeChannel"/> (no real sockets, no real time). They prove
/// text frames are rejected, valid binary ClientHello reaches the kernel and yields
/// a ServerWelcome, and unknown game payloads fail with a stable ServerError.
/// </summary>
public sealed class ProtobufRealtimeTransportTests
{
    private static readonly RealtimeProtobufCodec Codec = new();
    private static readonly RealtimeEnvelopeMapper Mapper = new();

    private static ProtobufRealtimeTransport Transport(FakeRealtimeChannel channel, string connectionId) =>
        new(channel, Codec, Mapper, new ConnectionId(connectionId));

    private static JoinTokenClaims Principal(string player) =>
        new("tenant-a", "demo-game", "room-1", player, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1));

    private static Wire.RealtimeEnvelope Hello(string player) => new()
    {
        ProtocolVersion = Wire.ProtocolVersion.V1,
        MessageType = Wire.MessageType.ClientHello,
        TenantId = "tenant-a",
        GameId = "demo-game",
        PlayerId = player,
        TraceId = "t-hello",
        ClientHello = new Wire.ClientHello
        {
            RequestedProtocolVersion = Wire.ProtocolVersion.V1,
            JoinToken = "join-token-1",
            ClientName = player,
        },
    };

    private static Wire.RealtimeEnvelope Ping(string player) => new()
    {
        ProtocolVersion = Wire.ProtocolVersion.V1,
        MessageType = Wire.MessageType.ClientPing,
        TenantId = "tenant-a",
        GameId = "demo-game",
        PlayerId = player,
        TraceId = "t-ping",
        ClientPing = new Wire.ClientPing { ClientTick = 1, Nonce = "n1" },
    };

    private static Wire.RealtimeEnvelope Command(string player) => new()
    {
        ProtocolVersion = Wire.ProtocolVersion.V1,
        MessageType = Wire.MessageType.ClientCommand,
        TenantId = "tenant-a",
        GameId = "demo-game",
        RoomId = "room-1",
        PlayerId = player,
        TraceId = "t-cmd",
        ClientCommand = new Wire.ClientCommand { Command = "MoveRight" },
    };

    [Fact]
    public async Task EdgeHandledFrames_AreCountedAsInboundActivity()
    {
        // A ping is answered at the edge (pong) and never forwarded; a command follows and is
        // forwarded. Both must count toward InboundFrameCount so the server can see that a client
        // sending only pings is alive — otherwise it would be wrongly reaped as idle.
        var channel = new FakeRealtimeChannel();
        var transport = Transport(channel, "c1");
        channel.EnqueueBinary(Codec.Encode(Ping("alice")));
        channel.EnqueueBinary(Codec.Encode(Command("alice")));
        channel.CompleteInbound();

        var message = await transport.ReceiveAsync(CancellationToken.None);

        Assert.NotNull(message);
        Assert.Equal(MessageType.ClientCommand, message!.MessageType);     // the command was forwarded
        Assert.Equal(2, transport.InboundFrameCount);                       // ping + command both counted
        Assert.Contains(channel.Sent.Select(Codec.Decode),                  // ping was answered at the edge
            m => m.PayloadCase == Wire.RealtimeEnvelope.PayloadOneofCase.ServerPong);
    }

    [Fact]
    public async Task WebSocketEndpoint_RejectsTextFrame()
    {
        var harness = new SliceHarness("tenant-a");
        var channel = new FakeRealtimeChannel();
        channel.EnqueueText(new byte[] { 1, 2, 3 });
        channel.CompleteInbound();

        await harness.Server.HandleConnectionAsync(Transport(channel, "c1"), Principal("alice"));

        var error = Assert.Single(channel.Sent.Select(Codec.Decode), m => m.PayloadCase == Wire.RealtimeEnvelope.PayloadOneofCase.ServerError);
        Assert.Equal(Wire.ErrorCode.TextFrameNotAllowed, error.ServerError.Code);
        Assert.True(channel.Closed);
        Assert.Equal(Wire.DisconnectReason.TextFrame, channel.CloseReason);
    }

    [Fact]
    public async Task WebSocketEndpoint_AcceptsBinaryClientHello()
    {
        var harness = new SliceHarness("tenant-a");
        var channel = new FakeRealtimeChannel();
        channel.EnqueueBinary(Codec.Encode(Hello("alice")));
        channel.CompleteInbound();

        await harness.Server.HandleConnectionAsync(Transport(channel, "c1"), Principal("alice"));

        Assert.DoesNotContain(channel.Sent.Select(Codec.Decode), m => m.PayloadCase == Wire.RealtimeEnvelope.PayloadOneofCase.ServerError);
        Assert.False(channel.Closed);
    }

    [Fact]
    public async Task WebSocketEndpoint_ReturnsServerWelcomeForValidHello()
    {
        var harness = new SliceHarness("tenant-a");
        var channel = new FakeRealtimeChannel();
        channel.EnqueueBinary(Codec.Encode(Hello("alice")));
        channel.CompleteInbound();

        await harness.Server.HandleConnectionAsync(Transport(channel, "c1"), Principal("alice"));

        var welcome = Assert.Single(channel.Sent.Select(Codec.Decode), m => m.PayloadCase == Wire.RealtimeEnvelope.PayloadOneofCase.ServerWelcome);
        Assert.Equal(Wire.ProtocolVersion.V1, welcome.ServerWelcome.AcceptedProtocolVersion);
        Assert.False(string.IsNullOrEmpty(welcome.ServerWelcome.SessionId));
    }

    [Fact]
    public void UnknownGamePayload_ReturnsStableServerError()
    {
        var env = new Wire.RealtimeEnvelope
        {
            ProtocolVersion = Wire.ProtocolVersion.V1,
            MessageType = Wire.MessageType.GameMessage,
            TenantId = "tenant-a",
            GameId = "demo-game",
            TraceId = "t-game",
            GameMessage = new Wire.GameMessage
            {
                GameMessageType = "custom.fireball",
                GamePayload = ByteString.CopyFrom(1, 2, 3),
                GameSchemaVersion = 2,
            },
        };

        var result = Mapper.MapClient(env);

        Assert.Equal(RealtimeClientDispatch.RespondImmediately, result.Dispatch);
        Assert.Equal(Wire.MessageType.ServerError, result.Response!.MessageType);
        Assert.Equal(Wire.ErrorCode.UnknownGameMessage, result.Response.ServerError.Code);
    }
}
