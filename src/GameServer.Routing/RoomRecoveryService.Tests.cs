using GameServer.Observability;
using GameServer.Observability.Testing;
using GameServer.Persistence;
using GameServer.Protocol;
using GameServer.Replication;
using GameServer.Simulation;
using GameServer.Simulation.Testing;

namespace GameServer.Routing;

/// <summary>
/// Behavioral contract tests for room recovery (restore from snapshot + event-log
/// replay). These assert externally visible outcomes — restored room state, replayed
/// counts, telemetry, recovery outcome, and tenant isolation — never engine internals.
/// State is opaque to the platform, so player position is read back through the game's
/// projection (<see cref="MoveRightGame.DecodeX"/>), exactly as a client would.
/// </summary>
public sealed class RoomRecoveryServiceTests
{
    private static readonly PlayerId Player = new("p1");
    private static readonly GameId Game = new("demo");
    private static readonly RoomId Arena = new("arena");

    private static RoomKey Key(string tenant) => new(new TenantId(tenant), Arena);

    private sealed class RecoveryFixture
    {
        public InMemorySnapshotStore<RoomKey, RoomSnapshot> Snapshots { get; } = new();
        public InMemoryEventLog<RoomKey, RoomEvent> Events { get; } = new();
        public TestTelemetrySink Telemetry { get; } = new();
        public RoomRecoveryService Service { get; }

        public RecoveryFixture()
        {
            Service = new RoomRecoveryService(
                Snapshots,
                Events,
                (roomId, _) => new GameRoom(roomId, new MoveRightGame(), new FakeSimulationClock(), new DeterministicRandomSource()),
                Telemetry);
        }
    }

    /// <summary>Player X read back from a room's projection (the opaque payload the client sees).</summary>
    private static int X(IGameRoom room) =>
        MoveRightGame.DecodeX(room.Project().Single(e => e.Id.Value == Player.Value).Payload);

    /// <summary>Builds an opaque snapshot for a room holding <paramref name="Player"/> at X via the real game.</summary>
    private static RoomSnapshot Snapshot(long tick, int x)
    {
        var game = new MoveRightGame();
        game.Join(Player);
        for (var i = 0; i < x; i++)
        {
            game.Apply(Player, MoveRightGame.MoveRight);
        }

        return new RoomSnapshot(tick, game.Serialize());
    }

    [Fact]
    public void RoomSnapshot_CanRestorePlayerPosition()
    {
        var fx = new RecoveryFixture();
        var key = Key("tenant-a");
        fx.Snapshots.Save(key, Snapshot(tick: 3, x: 4));

        var result = fx.Service.Restore(key, Game);

        Assert.Equal(RoomRecoveryOutcome.RestoredFromSnapshot, result.Outcome);
        Assert.NotNull(result.Room);
        Assert.Equal(4, X(result.Room!));
        Assert.Equal(3L, result.RestoredTick);
    }

    [Fact]
    public void RoomRestore_ReplaysEventsAfterSnapshot()
    {
        var fx = new RecoveryFixture();
        var key = Key("tenant-a");
        fx.Snapshots.Save(key, Snapshot(tick: 1, x: 1));
        fx.Events.Append(key, new RoomEvent(1, Player, MoveRightGame.MoveRight)); // already in snapshot
        fx.Events.Append(key, new RoomEvent(2, Player, MoveRightGame.MoveRight)); // after snapshot

        var result = fx.Service.Restore(key, Game);

        Assert.Equal(RoomRecoveryOutcome.RestoredFromSnapshot, result.Outcome);
        Assert.Equal(2, X(result.Room!)); // 1 from snapshot + 1 replayed
        Assert.Equal(1, result.ReplayedEventCount);                 // only the tick > 1 event
        Assert.Equal(2L, result.RestoredTick);
    }

    [Fact]
    public void RestoredRoom_ProducesSameStateAsOriginalRoom()
    {
        var fx = new RecoveryFixture();
        var key = Key("tenant-a");

        // Drive an original room, checkpointing an intermediate snapshot and every event.
        var original = new GameRoom(Arena, new MoveRightGame(), new FakeSimulationClock(), new DeterministicRandomSource());
        original.Join(Player);

        original.TryEnqueue(Player, MoveRightGame.MoveRight, 1);
        var tick1 = original.Tick();
        fx.Snapshots.Save(key, tick1.Snapshot); // snapshot taken at tick 1
        foreach (var e in tick1.Events) fx.Events.Append(key, e);

        original.TryEnqueue(Player, MoveRightGame.MoveRight, 2);
        var tick2 = original.Tick();
        foreach (var e in tick2.Events) fx.Events.Append(key, e); // events now span tick 1 and tick 2

        var result = fx.Service.Restore(key, Game);

        Assert.Equal(X(original), X(result.Room!));
        Assert.Equal(original.Snapshot().Tick, result.Room!.Snapshot().Tick);
    }

    [Fact]
    public void CrashRecovery_DoesNotDuplicateAlreadyAppliedCommand()
    {
        var fx = new RecoveryFixture();
        var key = Key("tenant-a");

        // Snapshot at tick 1 already includes the tick-1 MoveRight (position 1).
        fx.Snapshots.Save(key, Snapshot(tick: 1, x: 1));
        // The event log still contains that tick-1 event, plus a newer tick-2 event.
        fx.Events.Append(key, new RoomEvent(1, Player, MoveRightGame.MoveRight));
        fx.Events.Append(key, new RoomEvent(2, Player, MoveRightGame.MoveRight));

        var result = fx.Service.Restore(key, Game);

        // Must be 2 (snapshot + only tick-2 replay), NOT 3 (which would re-apply tick 1).
        Assert.Equal(2, X(result.Room!));
        Assert.Equal(1, result.ReplayedEventCount);
    }

    [Fact]
    public void RoomRestore_EmitsRoomRestoreTelemetry()
    {
        var fx = new RecoveryFixture();
        var key = Key("tenant-a");
        fx.Snapshots.Save(key, Snapshot(tick: 2, x: 2));
        fx.Events.Append(key, new RoomEvent(3, Player, MoveRightGame.MoveRight));

        fx.Service.Restore(key, Game);

        Assert.True(fx.Telemetry.HasEvent(TelemetryEvents.RoomRestored));
        var restored = fx.Telemetry.Events.Single(e => e.Name == TelemetryEvents.RoomRestored);
        Assert.NotNull(restored.Fields);
        Assert.Equal("tenant-a", restored.Fields!["tenantId"]);
        Assert.Equal("arena", restored.Fields!["roomId"]);
        Assert.Equal("demo", restored.Fields!["gameId"]);
        Assert.True(restored.Fields!.ContainsKey("restoredTick"));
        Assert.True(restored.Fields!.ContainsKey("replayedEventCount"));
        Assert.True(restored.Fields!.ContainsKey("outcome"));

        // The room_restore_count metric is part of the observability contract.
        Assert.Equal(1, fx.Telemetry.CountIncrements(TelemetryMetrics.RoomRestoreCount));
    }

    [Fact]
    public void RestoreMissingSnapshot_StartsEmptyRoomWhenPolicyAllows()
    {
        var fx = new RecoveryFixture();
        var key = Key("tenant-a"); // nothing stored

        var result = fx.Service.Restore(key, Game, MissingSnapshotPolicy.StartEmpty);

        Assert.Equal(RoomRecoveryOutcome.StartedEmpty, result.Outcome);
        Assert.NotNull(result.Room);
        Assert.Empty(result.Room!.Project());
        Assert.Equal(0L, result.RestoredTick);
        Assert.Equal(0, result.ReplayedEventCount);
    }

    [Fact]
    public void RestoreWithCorruptEventLog_ReturnsServerErrorOrRecoveryFailure()
    {
        var fx = new RecoveryFixture();
        var key = Key("tenant-a");
        fx.Snapshots.Save(key, Snapshot(tick: 0, x: 0));
        // An unrecognized command the game cannot apply.
        fx.Events.Append(key, new RoomEvent(1, Player, "Bogus"));

        var result = fx.Service.Restore(key, Game);

        // Corrupt data must fail explicitly and observably, not silently produce state.
        Assert.Equal(RoomRecoveryOutcome.Failed, result.Outcome);
    }

    [Fact]
    public void RestoreDoesNotCrossTenantBoundary()
    {
        var fx = new RecoveryFixture();
        var keyA = Key("tenant-a");
        var keyB = Key("tenant-b");

        // Only tenant A has recovery data for the (same-named) room.
        fx.Snapshots.Save(keyA, Snapshot(tick: 5, x: 9));
        fx.Events.Append(keyA, new RoomEvent(6, Player, MoveRightGame.MoveRight));

        // Restoring tenant B's room must never read tenant A's snapshot/events.
        var result = fx.Service.Restore(keyB, Game, MissingSnapshotPolicy.StartEmpty);

        Assert.Equal(RoomRecoveryOutcome.StartedEmpty, result.Outcome);
        Assert.NotNull(result.Room);
        Assert.Empty(result.Room!.Project()); // not tenant A's position 9
        Assert.Equal(0, result.ReplayedEventCount);       // not tenant A's event
    }
}
