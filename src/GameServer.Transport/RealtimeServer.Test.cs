using GameServer.Observability;
using GameServer.Protocol;
using GameServer.Routing;
using GameServer.Simulation;
using GameServer.Transport.Testing;

namespace GameServer.Transport;

/// <summary>
/// First batch of behavioral contract tests for the realtime kernel, exercised
/// end-to-end through <see cref="RealtimeServer"/> over the in-memory transport.
/// These assert externally visible outcomes only: server messages, authoritative
/// room state, event-log contents, telemetry, routing, and tenant isolation —
/// never private internals.
/// </summary>
public sealed class RealtimeServerBehaviorTests
{
    private static readonly RoomId Arena = new("arena");
    private static readonly PlayerId Player = new("p1");

    // State is opaque to the platform; read player X back through the move-right game's
    // projection / wire payload, exactly as a client would.
    private static int X(IGameRoom room) =>
        MoveRightGame.DecodeX(room.Project().Single(e => e.Id.Value == Player.Value).Payload);

    private static int StoredX(RoomSnapshot snapshot)
    {
        var game = new MoveRightGame();
        game.Restore(snapshot.State);
        return MoveRightGame.DecodeX(game.Project().Single(e => e.Id.Value == Player.Value).Payload);
    }

    private static int SnapshotX(ServerSnapshot snapshot) =>
        MoveRightGame.DecodeX(snapshot.Entities.Single(e => e.EntityId == Player.Value).Payload);

    // ---- Handshake: welcome / error / session gating -----------------------

    [Fact]
    public async Task ClientHello_WithValidTenant_ReturnsServerWelcome()
    {
        var harness = new SliceHarness("tenant-a");
        var (transport, client) = harness.NewClient("c1", "tenant-a", "p1");

        client.Hello();
        client.Close();
        await harness.Server.HandleConnectionAsync(transport, client.Principal);

        var welcome = Assert.IsType<ServerWelcome>(Assert.Single(client.Received()).Payload);
        Assert.Equal(ProtocolVersions.Current, welcome.AcceptedProtocolVersion);
        Assert.False(string.IsNullOrWhiteSpace(welcome.SessionId.Value));
        Assert.True(harness.Telemetry.HasEvent(TelemetryEvents.SessionCreated));
    }

    [Fact]
    public async Task ClientHello_WithInvalidTenant_ReturnsServerError()
    {
        var harness = new SliceHarness("tenant-a");
        var (transport, client) = harness.NewClient("c1", "tenant-unknown", "p1");

        client.Hello();
        client.Close();
        await harness.Server.HandleConnectionAsync(transport, client.Principal);

        var error = Assert.IsType<ServerError>(Assert.Single(client.Received()).Payload);
        Assert.Equal(ServerErrorCode.UnknownTenant, error.Code);
        Assert.True(harness.Telemetry.HasEvent(TelemetryEvents.TenantRejected));
        // An unknown tenant must not produce a session.
        Assert.DoesNotContain(client.Received(), m => m.Payload is ServerWelcome);
    }

    [Fact]
    public async Task CommandBeforeHandshake_IsRejected()
    {
        var harness = new SliceHarness("tenant-a");
        var (transport, client) = harness.NewClient("c1", "tenant-a", "p1");

        // No ClientHello / ClientJoinRoom: a command arrives before any handshake.
        client.Command(MoveRightGame.MoveRight, Arena);
        client.Close();
        await harness.Server.HandleConnectionAsync(transport, client.Principal);

        var error = Assert.IsType<ServerError>(Assert.Single(client.Received()).Payload);
        Assert.Equal(ServerErrorCode.NotJoined, error.Code);
        // Nothing should have been placed/created for this connection.
        Assert.False(harness.Router.TryGetRoom(harness.Key("tenant-a", "arena"), out _));
    }

    // ---- Routing & tenant isolation ----------------------------------------

    [Fact]
    public async Task SameRoomId_InDifferentTenants_CreatesDifferentRoomState()
    {
        var harness = new SliceHarness("tenant-a", "tenant-b");

        await RunAsync(harness, "ca", "tenant-a", c =>
        {
            c.Hello();
            c.Join(Arena);
            c.Command(MoveRightGame.MoveRight, Arena);
        });

        await RunAsync(harness, "cb", "tenant-b", c =>
        {
            c.Hello();
            c.Join(Arena);
        });

        await harness.Server.TickRoom(harness.Key("tenant-a", "arena"));
        await harness.Server.TickRoom(harness.Key("tenant-b", "arena"));

        Assert.True(harness.Router.TryGetRoom(harness.Key("tenant-a", "arena"), out var roomA));
        Assert.True(harness.Router.TryGetRoom(harness.Key("tenant-b", "arena"), out var roomB));
        Assert.NotSame(roomA, roomB);
        Assert.Equal(1, X(roomA));
        Assert.Equal(0, X(roomB));
    }

    [Fact]
    public async Task CommandRoutesToCorrectTenantGameRoom()
    {
        var harness = new SliceHarness("tenant-a", "tenant-b");

        await RunAsync(harness, "ca", "tenant-a", c =>
        {
            c.Hello();
            c.Join(Arena);
            c.Command(MoveRightGame.MoveRight, Arena);
        });
        await RunAsync(harness, "cb", "tenant-b", c =>
        {
            c.Hello();
            c.Join(Arena);
        });

        var keyA = harness.Key("tenant-a", "arena");
        var keyB = harness.Key("tenant-b", "arena");
        await harness.Server.TickRoom(keyA);
        await harness.Server.TickRoom(keyB);

        // The command must have landed only in tenant-a's arena room.
        Assert.Single(harness.Events.Read(keyA));
        Assert.Empty(harness.Events.Read(keyB));
    }

    // ---- Snapshot: authoritative state, delivered only to the right tenant --

    [Fact]
    public async Task TickProducesSnapshotWithAuthoritativeState()
    {
        var harness = new SliceHarness("tenant-a");
        var (transport, client) = harness.NewClient("c1", "tenant-a", "p1");

        await RunAsync(harness, transport, client, c =>
        {
            c.Hello();
            c.Join(Arena);
            c.Command(MoveRightGame.MoveRight, Arena);
        });

        var key = harness.Key("tenant-a", "arena");
        await harness.Server.TickRoom(key);

        var snapshot = client.Received().Select(m => m.Payload).OfType<ServerSnapshot>().Single();
        Assert.Equal(1L, snapshot.Tick);
        Assert.Equal(1, SnapshotX(snapshot));

        // The same authoritative state is durably checkpointed.
        Assert.True(harness.Snapshots.TryGetLatest(key, out var stored));
        Assert.Equal(1, StoredX(stored));
    }

    [Fact]
    public async Task SnapshotForTenantA_IsNeverDeliveredToTenantBConnection()
    {
        var harness = new SliceHarness("tenant-a", "tenant-b");

        var (transportA, clientA) = harness.NewClient("ca", "tenant-a", "p1");
        await RunAsync(harness, transportA, clientA, c =>
        {
            c.Hello();
            c.Join(Arena);
            c.Command(MoveRightGame.MoveRight, Arena);
        });

        var (transportB, clientB) = harness.NewClient("cb", "tenant-b", "p1");
        await RunAsync(harness, transportB, clientB, c =>
        {
            c.Hello();
            c.Join(Arena);
        });

        // Only tenant-a's room ticks and fans out a snapshot.
        await harness.Server.TickRoom(harness.Key("tenant-a", "arena"));

        var snapshotsToA = clientA.Received().Where(m => m.Payload is ServerSnapshot).ToList();
        var snapshotsToB = clientB.Received().Where(m => m.Payload is ServerSnapshot).ToList();

        Assert.Single(snapshotsToA);
        Assert.Equal(new TenantId("tenant-a"), snapshotsToA[0].TenantId);
        Assert.Empty(snapshotsToB);
    }

    // ---- Event log: accepted recorded, rejected not recorded as accepted ----

    [Fact]
    public async Task AcceptedCommand_IsWrittenToEventLog()
    {
        var harness = new SliceHarness("tenant-a");
        var (transport, client) = harness.NewClient("c1", "tenant-a", "p1");

        await RunAsync(harness, transport, client, c =>
        {
            c.Hello();
            c.Join(Arena);
            c.Command(MoveRightGame.MoveRight, Arena);
        });

        var key = harness.Key("tenant-a", "arena");
        await harness.Server.TickRoom(key);

        var logged = Assert.Single(harness.Events.Read(key));
        Assert.Equal(MoveRightGame.MoveRight, logged.Command);
        Assert.Equal(Player, logged.Player);
    }

    [Fact]
    public async Task RejectedCommand_IsNotWrittenAsAcceptedEvent()
    {
        var harness = new SliceHarness("tenant-a");
        var (transport, client) = harness.NewClient("c1", "tenant-a", "p1");

        await RunAsync(harness, transport, client, c =>
        {
            c.Hello();
            c.Join(Arena);
            c.Command(MoveRightGame.MoveRight, Arena, sequence: 10); // accepted
            c.Command(MoveRightGame.MoveRight, Arena, sequence: 10); // duplicate -> rejected
        });

        var key = harness.Key("tenant-a", "arena");
        await harness.Server.TickRoom(key);

        // Exactly one accepted command is recorded; the rejected one leaves no event.
        Assert.Single(harness.Events.Read(key));
        Assert.Contains(client.Received(), m => m.Payload is ServerError { Code: ServerErrorCode.StaleSequence });
    }

    // ---- helpers ------------------------------------------------------------

    private static Task RunAsync(SliceHarness harness, string connectionId, string tenantId, Action<FakeClient> script)
    {
        var (transport, client) = harness.NewClient(connectionId, tenantId, "p1");
        return RunAsync(harness, transport, client, script);
    }

    private static async Task RunAsync(
        SliceHarness harness,
        InMemoryBidirectionalTransport transport,
        FakeClient client,
        Action<FakeClient> script)
    {
        script(client);
        client.Close();
        await harness.Server.HandleConnectionAsync(transport, client.Principal);
    }
}
