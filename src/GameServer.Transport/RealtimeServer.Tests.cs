using GameServer.Observability;
using GameServer.Protocol;
using GameServer.Simulation;
using GameServer.Transport.Testing;
using Xunit;

namespace GameServer.Transport;

/// <summary>
/// Proves the first vertical slice end to end, entirely in-process:
/// connect → resolve tenant → negotiate version → session → join → command →
/// deterministic tick → authoritative state change → snapshot → client receives it.
/// Also covers tenant isolation and stale-sequence rejection through the server.
/// </summary>
public sealed class RealtimeServerTests
{
    private static readonly RoomId Arena = new("arena");

    [Fact]
    public async Task ValidClient_Connects_And_ReceivesWelcome()
    {
        var harness = new SliceHarness("tenant-a");
        var (transport, client) = harness.NewClient("c1", "tenant-a", "p1");

        client.Hello();
        client.Close();
        await harness.Server.HandleConnectionAsync(transport, client.Principal);

        var welcome = Assert.IsType<ServerWelcome>(Assert.Single(client.Received()).Payload);
        Assert.Equal(ProtocolVersions.Current, welcome.AcceptedProtocolVersion);
        Assert.False(string.IsNullOrWhiteSpace(welcome.SessionId.Value));
    }

    [Fact]
    public async Task Player_Can_Join_Room()
    {
        var harness = new SliceHarness("tenant-a");
        var (transport, client) = harness.NewClient("c1", "tenant-a", "p1");

        client.Hello();
        client.Join(Arena);
        client.Close();
        await harness.Server.HandleConnectionAsync(transport, client.Principal);

        Assert.True(harness.Router.TryGetRoom(harness.Key("tenant-a", "arena"), out var room));
        Assert.True(room.HasPlayer(new PlayerId("p1")));
        Assert.True(harness.Telemetry.HasEvent(TelemetryEvents.RoomJoined));
    }

    [Fact]
    public async Task MoveRight_IncrementsAuthoritativePosition_AfterOneTick_AndEmitsSnapshot()
    {
        var harness = new SliceHarness("tenant-a");
        var (transport, client) = harness.NewClient("c1", "tenant-a", "p1");

        client.Hello();
        client.Join(Arena);
        client.Command(ClientCommandType.MoveRight, Arena);
        client.Close();
        await harness.Server.HandleConnectionAsync(transport, client.Principal);

        var key = harness.Key("tenant-a", "arena");

        Assert.True(harness.Router.TryGetRoom(key, out var room));
        Assert.Equal(0, room.Snapshot().Positions[new PlayerId("p1")]);

        await harness.Server.TickRoom(key);

        Assert.Equal(1, room.Snapshot().Positions[new PlayerId("p1")]);

        var snapshot = client.Received().Select(m => m.Payload).OfType<ServerSnapshot>().Single();
        Assert.Equal(1L, snapshot.Tick);
        Assert.Equal(1, snapshot.Players.Single(p => p.PlayerId == new PlayerId("p1")).X);
    }

    [Fact]
    public async Task Tick_Persists_Snapshot_And_Event()
    {
        var harness = new SliceHarness("tenant-a");
        var (transport, client) = harness.NewClient("c1", "tenant-a", "p1");

        client.Hello();
        client.Join(Arena);
        client.Command(ClientCommandType.MoveRight, Arena);
        client.Close();
        await harness.Server.HandleConnectionAsync(transport, client.Principal);

        var key = harness.Key("tenant-a", "arena");
        await harness.Server.TickRoom(key);

        Assert.True(harness.Snapshots.TryGetLatest(key, out var stored));
        Assert.Equal(1, stored.Positions[new PlayerId("p1")]);

        var loggedEvent = Assert.Single(harness.Events.Read(key));
        Assert.Equal(RoomCommandType.MoveRight, loggedEvent.Command);
        Assert.True(harness.Telemetry.HasEvent(TelemetryEvents.SnapshotEmitted));
    }

    [Fact]
    public async Task UnsupportedProtocolVersion_IsRejected()
    {
        var harness = new SliceHarness("tenant-a");
        var (transport, client) = harness.NewClient("c1", "tenant-a", "p1");

        client.Hello(protocolVersion: ProtocolVersions.Current + 99);
        client.Close();
        await harness.Server.HandleConnectionAsync(transport, client.Principal);

        var error = Assert.IsType<ServerError>(Assert.Single(client.Received()).Payload);
        Assert.Equal(ServerErrorCode.UnsupportedProtocolVersion, error.Code);
    }

    [Fact]
    public async Task UnknownTenant_IsRejected_WithoutSession()
    {
        var harness = new SliceHarness("tenant-a");
        var (transport, client) = harness.NewClient("c1", "ghost-tenant", "p1");

        client.Hello();
        client.Close();
        await harness.Server.HandleConnectionAsync(transport, client.Principal);

        var error = Assert.IsType<ServerError>(Assert.Single(client.Received()).Payload);
        Assert.Equal(ServerErrorCode.UnknownTenant, error.Code);
        Assert.True(harness.Telemetry.HasEvent(TelemetryEvents.TenantRejected));
    }

    [Fact]
    public async Task TwoTenants_WithSameRoomId_DoNotShareState()
    {
        var harness = new SliceHarness("tenant-a", "tenant-b");

        var (transportA, clientA) = harness.NewClient("c-a", "tenant-a", "p1");
        clientA.Hello();
        clientA.Join(Arena);
        clientA.Command(ClientCommandType.MoveRight, Arena);
        clientA.Close();
        await harness.Server.HandleConnectionAsync(transportA, clientA.Principal);

        var (transportB, clientB) = harness.NewClient("c-b", "tenant-b", "p1");
        clientB.Hello();
        clientB.Join(Arena);
        clientB.Close();
        await harness.Server.HandleConnectionAsync(transportB, clientB.Principal);

        await harness.Server.TickRoom(harness.Key("tenant-a", "arena"));
        await harness.Server.TickRoom(harness.Key("tenant-b", "arena"));

        var snapshotA = clientA.Received().Select(m => m.Payload).OfType<ServerSnapshot>().Single();
        var snapshotB = clientB.Received().Select(m => m.Payload).OfType<ServerSnapshot>().Single();

        Assert.Equal(1, snapshotA.Players.Single().X); // tenant A moved
        Assert.Equal(0, snapshotB.Players.Single().X); // tenant B unaffected — isolated room state
    }

    [Fact]
    public async Task StaleSequence_IsRejected_ThroughServer()
    {
        var harness = new SliceHarness("tenant-a");
        var (transport, client) = harness.NewClient("c1", "tenant-a", "p1");

        client.Hello();
        client.Join(Arena);
        client.Command(ClientCommandType.MoveRight, Arena, sequence: 10); // accepted
        client.Command(ClientCommandType.MoveRight, Arena, sequence: 10); // stale
        client.Close();
        await harness.Server.HandleConnectionAsync(transport, client.Principal);

        var error = client.Received().Select(m => m.Payload).OfType<ServerError>().Single();
        Assert.Equal(ServerErrorCode.StaleSequence, error.Code);

        var key = harness.Key("tenant-a", "arena");
        await harness.Server.TickRoom(key);

        Assert.True(harness.Router.TryGetRoom(key, out var room));
        Assert.Equal(1, room.Snapshot().Positions[new PlayerId("p1")]); // only first applied
    }
}
