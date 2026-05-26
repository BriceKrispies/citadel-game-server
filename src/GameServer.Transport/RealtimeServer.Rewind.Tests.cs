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
/// Tests live single-room rewind: rebuilding a room's authoritative state as of a past tick, forking
/// the timeline (discarding the invalid future), and resuming. Asserts externally visible outcomes —
/// the observed tick/state, the retained event log, the resumed timeline, and rejection cases.
/// </summary>
public sealed class RealtimeServerRewindTests
{
    private static readonly RoomId Arena = new("arena");
    private static readonly PlayerId Player = new("p1");

    /// <summary>
    /// A rewind-capable wiring: a history snapshot store + rewindable event log + the replay engine, all
    /// sharing the room factory the router uses (so a rebuilt room is an honest swap-in).
    /// </summary>
    private sealed class RewindHarness
    {
        public InMemoryTenantResolver Tenants { get; }
        public InMemorySessionRouter Router { get; }
        public InMemorySnapshotHistoryStore<RoomKey, RoomSnapshot> History { get; }
        public InMemoryEventLog<RoomKey, RoomEvent> Events { get; }
        public TestTelemetrySink Telemetry { get; }
        public RealtimeServer Server { get; }

        public RewindHarness(int rewindHorizonTicks = 0)
        {
            Tenants = new InMemoryTenantResolver(new[] { new TenantContext(new TenantId("tenant-a"), "Tenant A") });
            GameRoomFactory factory = (roomId, _) =>
                new GameRoom(roomId, new MoveRightGame(), new FakeSimulationClock(), new DeterministicRandomSource());
            Router = new InMemorySessionRouter(factory);
            History = new InMemorySnapshotHistoryStore<RoomKey, RoomSnapshot>(s => s.Tick);
            Events = new InMemoryEventLog<RoomKey, RoomEvent>(e => e.Tick);
            Telemetry = new TestTelemetrySink();
            var replay = new RoomReplayService(History, Events, factory, Telemetry);
            Server = new RealtimeServer(
                Tenants, Router, History, Events, Telemetry,
                eventLogRetentionTicks: rewindHorizonTicks, replay: replay);
        }

        public RoomKey Key => new(new TenantId("tenant-a"), Arena);

        /// <summary>Joins p1 through the real edge (registers the room + its game), then closes — the room
        /// persists. Returns after the connection is fully processed.</summary>
        public async Task JoinAndDisconnect()
        {
            var transport = new InMemoryBidirectionalTransport(new ConnectionId("c1"));
            var client = new FakeClient(transport, new TenantId("tenant-a"), new GameId(SliceHarness.DefaultGame), Player, Arena);
            client.Hello();
            client.Join(Arena);
            client.Close();
            await Server.HandleConnectionAsync(transport, client.Principal);
        }

        /// <summary>Advances the live room <paramref name="ticks"/> ticks, one MoveRight per tick, so X == tick.</summary>
        public async Task DriveMoves(int ticks)
        {
            Router.TryGetRoom(Key, out var room);
            room.Join(Player); // idempotent; ensures membership for direct enqueue
            for (var seq = 1; seq <= ticks; seq++)
            {
                room.TryEnqueue(Player, MoveRightGame.MoveRight, seq);
                await Server.TickRoom(Key);
            }
        }
    }

    private static int ObservedX(RoomObservation observation) =>
        (int)observation.Entities.Single(e => e.EntityId == Player.Value).X;

    [Fact]
    public async Task RewindRoom_RestoresPastTick_DiscardsFuture_AndResumesOnForkedTimeline()
    {
        var h = new RewindHarness();
        await h.JoinAndDisconnect();
        await h.DriveMoves(5); // X == 5 at tick 5

        Assert.True(h.Server.TryObserveRoom(h.Key, out var before));
        Assert.Equal(5L, before.Tick);
        Assert.Equal(5, ObservedX(before));

        var outcome = h.Server.RewindRoom(h.Key, targetTick: 2, reason: "test");

        Assert.Equal(RoomRewindOutcome.Rewound, outcome);
        Assert.True(h.Server.TryObserveRoom(h.Key, out var after));
        Assert.Equal(2L, after.Tick);       // clock rewound exactly to the target
        Assert.Equal(2, ObservedX(after));  // state as of tick 2

        // The invalid future is gone from the authoritative log: only ticks 1..2 remain.
        Assert.Equal(new long[] { 1, 2 }, h.Events.Read(h.Key).Select(e => e.Tick).Distinct().ToArray());

        // The timeline resumes from the rewind point: a new command advances to tick 3 (X == 3).
        h.Router.TryGetRoom(h.Key, out var rewound);
        Assert.Equal(CommandAdmission.Accepted, rewound.TryEnqueue(Player, MoveRightGame.MoveRight, sequence: 1));
        await h.Server.TickRoom(h.Key);

        Assert.True(h.Server.TryObserveRoom(h.Key, out var resumed));
        Assert.Equal(3L, resumed.Tick);
        Assert.Equal(3, ObservedX(resumed));
    }

    [Fact]
    public async Task RewindRoom_EmitsRewindTelemetry()
    {
        var h = new RewindHarness();
        await h.JoinAndDisconnect();
        await h.DriveMoves(3);

        h.Server.RewindRoom(h.Key, targetTick: 1, reason: "operator-undo");

        Assert.True(h.Telemetry.HasEvent(TelemetryEvents.RoomRewound));
        var rewound = h.Telemetry.Events.Single(e => e.Name == TelemetryEvents.RoomRewound);
        Assert.Equal("tenant-a", rewound.Fields!["tenantId"]);
        Assert.Equal("arena", rewound.Fields!["roomId"]);
        Assert.Equal("1", rewound.Fields!["toTick"]);
        Assert.Equal("operator-undo", rewound.Fields!["reason"]);
        Assert.Equal(1, h.Telemetry.CountIncrements(TelemetryMetrics.RoomRewindCount));
    }

    [Fact]
    public void RewindRoom_AbsentRoom_ReturnsNotFound()
    {
        var h = new RewindHarness();

        Assert.Equal(RoomRewindOutcome.NotFound, h.Server.RewindRoom(h.Key, targetTick: 0, reason: "x"));
    }

    [Fact]
    public async Task RewindRoom_TargetBeyondHorizon_IsRejected()
    {
        // Horizon of 2 ticks: after driving to tick 5, the earliest retained checkpoint is past tick 1.
        var h = new RewindHarness(rewindHorizonTicks: 2);
        await h.JoinAndDisconnect();
        await h.DriveMoves(5);

        var outcome = h.Server.RewindRoom(h.Key, targetTick: 1, reason: "too-far");

        Assert.Equal(RoomRewindOutcome.BeyondHorizon, outcome);
        // The room is untouched: still at tick 5.
        Assert.True(h.Server.TryObserveRoom(h.Key, out var observation));
        Assert.Equal(5L, observation.Tick);
    }

    [Fact]
    public async Task RewindRoom_WithoutReplayWired_IsNotRewindable()
    {
        // The default slice harness wires only a latest-only store and no replay engine.
        var slice = new SliceHarness("tenant-a");
        var (transport, client) = slice.NewClient("c1", "tenant-a", "p1");
        client.Hello();
        client.Join(Arena);
        client.Close();
        await slice.Server.HandleConnectionAsync(transport, client.Principal);
        await slice.Server.TickRoom(slice.Key("tenant-a", "arena"));

        var outcome = slice.Server.RewindRoom(slice.Key("tenant-a", "arena"), targetTick: 0, reason: "x");

        Assert.Equal(RoomRewindOutcome.NotRewindable, outcome);
    }
}
