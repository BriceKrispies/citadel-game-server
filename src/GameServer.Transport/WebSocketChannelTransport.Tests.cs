using GameServer.Identity;
using GameServer.Observability;
using GameServer.Protocol;
using GameServer.Transport.Testing;

namespace GameServer.Transport;

/// <summary>
/// Behavioral tests for the WebSocket text-frame adapter, driven in-process through
/// a <see cref="FakeWebSocketChannel"/> (no real sockets, no real time). They prove
/// frames are decoded into the kernel, server messages come back as JSON, multiple
/// connections in a room all see authoritative moves, dropped connections stop
/// receiving, and malformed input is answered with a ServerError.
/// </summary>
public sealed class WebSocketChannelTransportTests
{
    private static readonly JsonMessageCodec Codec = new();
    private const string Tenant = "tenant-a";
    private const string Game = "demo-game";

    [Fact]
    public async Task WebSocketAdapter_DecodesClientHello_AndPassesToRealtimeServer()
    {
        var harness = new SliceHarness(Tenant);
        var channel = new FakeWebSocketChannel();
        channel.EnqueueInbound(Codec.Encode(Hello("pA", 1)));
        channel.CompleteInbound();

        await harness.Server.HandleConnectionAsync(Transport(channel, "c1"), Principal("pA"));

        // The decoded hello reached the kernel: a session was created.
        Assert.True(harness.Telemetry.HasEvent(TelemetryEvents.SessionCreated));
        Assert.Contains(Decoded(channel), m => m.Payload is ServerWelcome);
    }

    [Fact]
    public async Task WebSocketAdapter_SendsServerWelcome_AsJson()
    {
        var harness = new SliceHarness(Tenant);
        var channel = new FakeWebSocketChannel();
        channel.EnqueueInbound(Codec.Encode(Hello("pA", 1)));
        channel.CompleteInbound();

        await harness.Server.HandleConnectionAsync(Transport(channel, "c1"), Principal("pA"));

        var frame = Assert.Single(channel.Sent);
        Assert.StartsWith("{", frame.TrimStart()); // it is JSON text
        var decoded = Codec.Decode(frame);
        Assert.Equal(MessageType.ServerWelcome, decoded.MessageType);
        Assert.IsType<ServerWelcome>(decoded.Payload);
    }

    [Fact]
    public async Task TwoConnections_JoinSameRoom_BothReceiveSnapshotAfterMove()
    {
        var harness = new SliceHarness(Tenant);
        var room = new RoomId("room-1");

        var ch1 = new FakeWebSocketChannel();
        ch1.EnqueueInbound(Codec.Encode(Hello("pA", 1)));
        ch1.EnqueueInbound(Codec.Encode(Join("pA", 2, room)));
        ch1.EnqueueInbound(Codec.Encode(Move("pA", 3, room)));
        ch1.CompleteInbound();
        await harness.Server.HandleConnectionAsync(Transport(ch1, "c1"), Principal("pA"));

        var ch2 = new FakeWebSocketChannel();
        ch2.EnqueueInbound(Codec.Encode(Hello("pB", 1)));
        ch2.EnqueueInbound(Codec.Encode(Join("pB", 2, room)));
        ch2.CompleteInbound();
        await harness.Server.HandleConnectionAsync(Transport(ch2, "c2"), Principal("pB"));

        await harness.Server.TickRoom(harness.Key(Tenant, "room-1"));

        // Both connections see player pA's authoritative position advance to 1.
        Assert.Contains(Decoded(ch1), m => IsSnapshotFor(m, "pA", x: 1));
        Assert.Contains(Decoded(ch2), m => IsSnapshotFor(m, "pA", x: 1));
    }

    [Fact]
    public async Task DisconnectedConnection_DoesNotReceiveFurtherSnapshots()
    {
        var harness = new SliceHarness(Tenant);
        var room = new RoomId("room-1");

        var ch1 = new FakeWebSocketChannel();
        ch1.EnqueueInbound(Codec.Encode(Hello("pA", 1)));
        ch1.EnqueueInbound(Codec.Encode(Join("pA", 2, room)));
        ch1.CompleteInbound();
        await harness.Server.HandleConnectionAsync(Transport(ch1, "c1"), Principal("pA"));

        var ch2 = new FakeWebSocketChannel();
        ch2.EnqueueInbound(Codec.Encode(Hello("pB", 1)));
        ch2.EnqueueInbound(Codec.Encode(Join("pB", 2, room)));
        ch2.EnqueueInbound(Codec.Encode(Move("pB", 3, room)));
        ch2.CompleteInbound();
        await harness.Server.HandleConnectionAsync(Transport(ch2, "c2"), Principal("pB"));

        ch1.Disconnect(); // conn1 is gone before the tick fans out

        await harness.Server.TickRoom(harness.Key(Tenant, "room-1"));

        Assert.DoesNotContain(Decoded(ch1), m => m.Payload is ServerSnapshot);
        Assert.Contains(Decoded(ch2), m => m.Payload is ServerSnapshot);
    }

    [Fact]
    public async Task MalformedJson_ReturnsServerError()
    {
        var harness = new SliceHarness(Tenant);
        var channel = new FakeWebSocketChannel();
        channel.EnqueueInbound("{ this is not valid json");
        channel.CompleteInbound();

        await harness.Server.HandleConnectionAsync(Transport(channel, "c1"), Principal("pA"));

        var error = Assert.Single(Decoded(channel), m => m.Payload is ServerError);
        Assert.Equal(ServerErrorCode.MalformedMessage, ((ServerError)error.Payload).Code);
    }

    // ---- helpers ------------------------------------------------------------

    private static WebSocketChannelTransport Transport(IWebSocketChannel channel, string connectionId) =>
        new(channel, Codec, new ConnectionId(connectionId));

    // The verified identity the edge would derive from a join token, matching the
    // tenant/game these tests declare. Room defaults to the room they join.
    private static JoinTokenClaims Principal(string player, string room = "room-1") =>
        new(Tenant, Game, room, player, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1));

    private static IEnumerable<MessageEnvelope> Decoded(FakeWebSocketChannel channel) =>
        channel.Sent.Select(Codec.Decode);

    private static bool IsSnapshotFor(MessageEnvelope envelope, string player, int x) =>
        envelope.Payload is ServerSnapshot snapshot
        && snapshot.Players.Any(p => p.PlayerId == new PlayerId(player) && p.X == x);

    private static MessageEnvelope Hello(string player, long sequence) =>
        Envelope(MessageType.ClientHello, new ClientHello(player), player, sequence, room: null);

    private static MessageEnvelope Join(string player, long sequence, RoomId room) =>
        Envelope(MessageType.ClientJoinRoom, new ClientJoinRoom(room), player, sequence, room);

    private static MessageEnvelope Move(string player, long sequence, RoomId room) =>
        Envelope(MessageType.ClientCommand, new ClientCommand(ClientCommandType.MoveRight), player, sequence, room);

    private static MessageEnvelope Envelope(
        MessageType type, IMessagePayload payload, string player, long sequence, RoomId? room) =>
        new()
        {
            TenantId = new TenantId(Tenant),
            GameId = new GameId(Game),
            RoomId = room,
            PlayerId = new PlayerId(player),
            ProtocolVersion = ProtocolVersions.Current,
            MessageType = type,
            Sequence = sequence,
            TraceId = $"t-{type}-{sequence}",
            Payload = payload,
        };
}
