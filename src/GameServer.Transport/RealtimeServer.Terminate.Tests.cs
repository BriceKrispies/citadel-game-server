using GameServer.Observability;
using GameServer.Observability.Testing;
using GameServer.Persistence;
using GameServer.Protocol;
using GameServer.Routing;
using GameServer.Simulation;
using GameServer.Simulation.Testing;
using GameServer.Tenancy;
using GameServer.Transport.Testing;
using Xunit;

namespace GameServer.Transport;

/// <summary>
/// Proves the administrative room terminate goes through the AUTHORITATIVE pathway (the game's
/// <c>OnTerminate</c>, the same teardown a reap performs) and leaves NO orphan state: the room drops out
/// of <see cref="RealtimeServer.ActiveRooms"/>, can no longer be observed, and the termination is
/// observable in telemetry with the operator's reason.
/// </summary>
public sealed class RealtimeServerTerminateTests
{
    private static readonly RoomId Arena = new("arena");

    // A server wired so the test can capture the game instance behind a room and observe OnTerminate.
    private sealed class CapturingHarness
    {
        public readonly Dictionary<RoomKey, MoveRightGame> Games = new();
        public readonly TestTelemetrySink Telemetry = new();
        public readonly RealtimeServer Server;

        public CapturingHarness()
        {
            var tenants = new InMemoryTenantResolver(new[] { new TenantContext(new TenantId("tenant-a"), "Tenant A") });
            var router = new InMemorySessionRouter((roomId, gameId) =>
            {
                var game = new MoveRightGame();
                var room = new GameRoom(roomId, game, new FakeSimulationClock(), new DeterministicRandomSource());
                Games[new RoomKey(new TenantId("tenant-a"), roomId)] = game;
                return room;
            });
            Server = new RealtimeServer(
                tenants, router,
                new InMemorySnapshotStore<RoomKey, RoomSnapshot>(),
                new InMemoryEventLog<RoomKey, RoomEvent>(),
                Telemetry,
                lifecycle: RoomLifecycle.Persist);
        }
    }

    [Fact]
    public async Task TerminateRoom_FiresOnTerminate_AndLeavesNoOrphanState()
    {
        var harness = new CapturingHarness();
        var transport = new InMemoryBidirectionalTransport(new ConnectionId("c1"));
        var client = new FakeClient(transport, new TenantId("tenant-a"), new GameId("demo"), new PlayerId("p1"), Arena);
        client.Hello();
        client.Join(Arena);
        client.Close();
        await harness.Server.HandleConnectionAsync(transport, client.Principal);

        var key = harness.Server.ActiveRooms.Single();
        Assert.True(harness.Server.TryObserveRoom(key, out _));
        Assert.False(harness.Games[key].Terminated);

        var terminated = harness.Server.TerminateRoom(key, "operator-terminate");

        Assert.True(terminated);
        // Authoritative path: the game's OnTerminate fired (not a side mutation of room state).
        Assert.True(harness.Games[key].Terminated);
        // No orphan state: gone from ActiveRooms and no longer observable.
        Assert.DoesNotContain(key, harness.Server.ActiveRooms);
        Assert.False(harness.Server.TryObserveRoom(key, out _));
        // Observable termination carrying the operator reason.
        Assert.Contains(harness.Telemetry.Events, e =>
            e.Name == TelemetryEvents.RoomClosed
            && e.Fields is not null
            && e.Fields.TryGetValue("reason", out var reason) && reason == "operator-terminate");
    }

    [Fact]
    public void TerminateRoom_UnknownRoom_ReturnsFalse()
    {
        var harness = new CapturingHarness();

        Assert.False(harness.Server.TerminateRoom(new RoomKey(new TenantId("tenant-a"), new RoomId("nope")), "x"));
    }

    [Fact]
    public async Task TerminateRoom_IsIdempotent_SecondCallReturnsFalse()
    {
        var harness = new CapturingHarness();
        var transport = new InMemoryBidirectionalTransport(new ConnectionId("c1"));
        var client = new FakeClient(transport, new TenantId("tenant-a"), new GameId("demo"), new PlayerId("p1"), Arena);
        client.Hello();
        client.Join(Arena);
        client.Close();
        await harness.Server.HandleConnectionAsync(transport, client.Principal);
        var key = harness.Server.ActiveRooms.Single();

        Assert.True(harness.Server.TerminateRoom(key, "first"));
        Assert.False(harness.Server.TerminateRoom(key, "second"));
    }
}
