using GameServer.Observability.Testing;
using GameServer.Persistence;
using GameServer.Protocol;
using GameServer.Simulation;
using GameServer.Simulation.Testing;
using Xunit;

namespace GameServer.Routing;

/// <summary>
/// Contract tests for the step-through replay session: walking a room's recorded history one tick at
/// a time must reach the same authoritative state as a single <see cref="RoomReplayService.ReplayTo"/>.
/// </summary>
public sealed class ReplaySessionTests
{
    private static readonly PlayerId Player = new("p1");
    private static readonly GameId Game = new("demo");
    private static readonly RoomId Arena = new("arena");
    private static readonly RoomKey Key = new(new TenantId("tenant-a"), Arena);

    private static int X(IReadOnlyList<Replication.EntitySnapshot> world) =>
        MoveRightGame.DecodeX(world.Single(e => e.Id.Value == Player.Value).Payload);

    private static RoomReplayService BuildServiceWithHistory(
        out InMemorySnapshotHistoryStore<RoomKey, RoomSnapshot> history,
        out InMemoryEventLog<RoomKey, RoomEvent> events)
    {
        history = new InMemorySnapshotHistoryStore<RoomKey, RoomSnapshot>(s => s.Tick);
        events = new InMemoryEventLog<RoomKey, RoomEvent>(e => e.Tick);

        var live = new GameRoom(Arena, new MoveRightGame(), new FakeSimulationClock(), new DeterministicRandomSource());
        live.Join(Player);
        history.Save(Key, live.Snapshot()); // checkpoint at tick 0
        for (var seq = 1; seq <= 5; seq++)
        {
            live.TryEnqueue(Player, MoveRightGame.MoveRight, seq);
            foreach (var e in live.Tick().Events)
            {
                events.Append(Key, e);
            }
        }

        return new RoomReplayService(
            history,
            events,
            (roomId, _) => new GameRoom(roomId, new MoveRightGame(), new FakeSimulationClock(), new DeterministicRandomSource()),
            new TestTelemetrySink());
    }

    [Fact]
    public void Stepping_TickByTick_ReachesSameStateAs_ReplayTo()
    {
        var service = BuildServiceWithHistory(out _, out _);

        var session = service.OpenSession(Key, Game, throughTick: 5);
        Assert.NotNull(session);
        Assert.Equal(0L, session!.CurrentTick); // starts at the checkpoint

        long lastTick = 0;
        var steps = 0;
        while (session.Step())
        {
            steps++;
            Assert.True(session.CurrentTick > lastTick); // advances monotonically, one recorded tick per step
            lastTick = session.CurrentTick;
        }

        Assert.Equal(5, steps);
        Assert.Equal(5L, session.CurrentTick);

        var oneShot = service.ReplayTo(Key, Game, targetTick: 5);
        Assert.Equal(X(oneShot.Room!.Project()), X(session.Project()));
    }

    [Fact]
    public void Step_AtEndOfHistory_ReturnsFalse()
    {
        var service = BuildServiceWithHistory(out _, out _);
        var session = service.OpenSession(Key, Game, throughTick: 2)!;

        Assert.True(session.Step());  // tick 1
        Assert.True(session.Step());  // tick 2
        Assert.False(session.Step()); // no more loaded history
        Assert.Equal(2L, session.CurrentTick);
        Assert.Equal(2, X(session.Project()));
    }

    [Fact]
    public void OpenSession_BeyondHorizon_ReturnsNull()
    {
        var history = new InMemorySnapshotHistoryStore<RoomKey, RoomSnapshot>(s => s.Tick);
        var events = new InMemoryEventLog<RoomKey, RoomEvent>(e => e.Tick);
        history.Save(Key, new RoomSnapshot(10, new MoveRightGame().Serialize()));
        var service = new RoomReplayService(
            history,
            events,
            (roomId, _) => new GameRoom(roomId, new MoveRightGame(), new FakeSimulationClock(), new DeterministicRandomSource()),
            new TestTelemetrySink());

        Assert.Null(service.OpenSession(Key, Game, throughTick: 3));
    }
}
