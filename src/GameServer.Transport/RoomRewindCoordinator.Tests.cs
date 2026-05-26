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
/// Tests bulk rewind across many rooms: per-tenant scope stays within the tenant boundary, the global
/// scope covers every room, and one room's failure never aborts the batch.
/// </summary>
public sealed class RoomRewindCoordinatorTests
{
    private static readonly PlayerId Player = new("p1");

    private sealed class MultiRoomHarness
    {
        public InMemorySessionRouter Router { get; }
        public RealtimeServer Server { get; }
        public RoomRewindCoordinator Coordinator { get; }
        public TestTelemetrySink Telemetry { get; } = new();
        private readonly TickGate _gate = new();
        private int _conn;

        public MultiRoomHarness()
        {
            var tenants = new InMemoryTenantResolver(new[]
            {
                new TenantContext(new TenantId("tenant-a"), "Tenant A"),
                new TenantContext(new TenantId("tenant-b"), "Tenant B"),
            });
            GameRoomFactory factory = (roomId, _) =>
                new GameRoom(roomId, new MoveRightGame(), new FakeSimulationClock(), new DeterministicRandomSource());
            Router = new InMemorySessionRouter(factory);
            var history = new InMemorySnapshotHistoryStore<RoomKey, RoomSnapshot>(s => s.Tick);
            var events = new InMemoryEventLog<RoomKey, RoomEvent>(e => e.Tick);
            var replay = new RoomReplayService(history, events, factory, Telemetry);
            Server = new RealtimeServer(tenants, Router, history, events, Telemetry, replay: replay);
            Coordinator = new RoomRewindCoordinator(Server, Router, _gate, Telemetry);
        }

        public RoomKey Key(string tenant, string room) => new(new TenantId(tenant), new RoomId(room));

        /// <summary>Places a room (via a real join) and advances it to <paramref name="ticks"/>.</summary>
        public async Task PlaceAndDrive(string tenant, string room, int ticks)
        {
            var transport = new InMemoryBidirectionalTransport(new ConnectionId($"c{++_conn}"));
            var client = new FakeClient(transport, new TenantId(tenant), new GameId(SliceHarness.DefaultGame), Player, new RoomId(room));
            client.Hello();
            client.Join(new RoomId(room));
            client.Close();
            await Server.HandleConnectionAsync(transport, client.Principal);

            var key = Key(tenant, room);
            Router.TryGetRoom(key, out var placed);
            placed.Join(Player);
            for (var seq = 1; seq <= ticks; seq++)
            {
                placed.TryEnqueue(Player, MoveRightGame.MoveRight, seq);
                await Server.TickRoom(key);
            }
        }

        public long ObservedTick(string tenant, string room)
        {
            Assert.True(Server.TryObserveRoom(Key(tenant, room), out var observation));
            return observation.Tick;
        }
    }

    [Fact]
    public async Task RewindTenant_RewindsOnlyThatTenantsRooms_LeavingOtherTenantUntouched()
    {
        var h = new MultiRoomHarness();
        await h.PlaceAndDrive("tenant-a", "arena-1", ticks: 5);
        await h.PlaceAndDrive("tenant-a", "arena-2", ticks: 4);
        await h.PlaceAndDrive("tenant-b", "arena-1", ticks: 5);

        var report = h.Coordinator.RewindTenant(new TenantId("tenant-a"), new RewindByTicks(3), reason: "tenant-undo");

        Assert.Equal(2, report.Total);
        Assert.Equal(2, report.Rewound);
        Assert.Equal(2, h.ObservedTick("tenant-a", "arena-1")); // 5 - 3
        Assert.Equal(1, h.ObservedTick("tenant-a", "arena-2")); // 4 - 3
        Assert.Equal(5, h.ObservedTick("tenant-b", "arena-1")); // untouched — isolation
    }

    [Fact]
    public async Task RewindAll_RewindsEveryRoom_AcrossTenants()
    {
        var h = new MultiRoomHarness();
        await h.PlaceAndDrive("tenant-a", "arena-1", ticks: 6);
        await h.PlaceAndDrive("tenant-b", "arena-1", ticks: 6);

        var report = h.Coordinator.RewindAll(new RewindToTick(2), reason: "platform-rollback");

        Assert.Equal(2, report.Total);
        Assert.Equal(2, report.Rewound);
        Assert.Equal(2, h.ObservedTick("tenant-a", "arena-1"));
        Assert.Equal(2, h.ObservedTick("tenant-b", "arena-1"));
    }

    [Fact]
    public async Task RewindMany_IsolatesPerRoomOutcome_AndKeepsGoing()
    {
        var h = new MultiRoomHarness();
        await h.PlaceAndDrive("tenant-a", "arena-1", ticks: 5);
        var live = h.Key("tenant-a", "arena-1");
        var missing = h.Key("tenant-a", "ghost"); // never placed

        var report = h.Coordinator.RewindMany(new[] { missing, live }, new RewindToTick(2), reason: "mixed");

        Assert.Equal(2, report.Total);
        Assert.Equal(1, report.Rewound);
        Assert.Equal(1, report.Skipped); // the missing room, recorded — not an abort
        Assert.Equal(2, h.ObservedTick("tenant-a", "arena-1")); // the live room still rewound
        Assert.Equal(RoomRewindOutcome.NotFound, report.Entries.Single(e => e.Key == missing).Outcome);
    }
}
