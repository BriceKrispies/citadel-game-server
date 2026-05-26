using System.Buffers.Binary;
using System.Text.Json;
using GameServer.Observability;
using GameServer.Observability.Testing;
using GameServer.Persistence;
using GameServer.Protocol;
using GameServer.Replication;
using GameServer.Simulation;
using GameServer.Simulation.Testing;
using Xunit;

namespace GameServer.Routing;

/// <summary>
/// Behavioral contract tests for the replay engine: reconstructing a room as of an arbitrary past
/// tick from a floor checkpoint plus recorded events. Assertions read externally visible state
/// through the game's projection (as a client would), never engine internals.
/// </summary>
public sealed class RoomReplayServiceTests
{
    private static readonly PlayerId Player = new("p1");
    private static readonly GameId Game = new("demo");
    private static readonly RoomId Arena = new("arena");

    private static RoomKey Key(string tenant) => new(new TenantId(tenant), Arena);

    private sealed class ReplayFixture
    {
        public InMemorySnapshotHistoryStore<RoomKey, RoomSnapshot> History { get; } = new(s => s.Tick);
        public InMemoryEventLog<RoomKey, RoomEvent> Events { get; } = new(e => e.Tick);
        public TestTelemetrySink Telemetry { get; } = new();
        public RoomReplayService Service { get; }

        public ReplayFixture()
        {
            Service = new RoomReplayService(
                History,
                Events,
                (roomId, _) => new GameRoom(roomId, new MoveRightGame(), new FakeSimulationClock(), new DeterministicRandomSource()),
                Telemetry);
        }

        public GameRoom NewMoveRightRoom() =>
            new(Arena, new MoveRightGame(), new FakeSimulationClock(), new DeterministicRandomSource());
    }

    private static int X(IGameRoom room) =>
        MoveRightGame.DecodeX(room.Project().Single(e => e.Id.Value == Player.Value).Payload);

    [Fact]
    public void ReplayTo_InteriorTicks_MatchLiveRoom_FromFloorCheckpointPlusEventReplay()
    {
        var fx = new ReplayFixture();
        var key = Key("tenant-a");

        // Drive a live room six ticks. Checkpoint ONLY at ticks 0 and 3, so the other ticks must be
        // reconstructed from the floor checkpoint plus replayed events. Append every event.
        var live = fx.NewMoveRightRoom();
        live.Join(Player);
        fx.History.Save(key, live.Snapshot()); // checkpoint at tick 0
        var expectedX = new Dictionary<long, int> { [0] = 0 };
        for (var seq = 1; seq <= 6; seq++)
        {
            live.TryEnqueue(Player, MoveRightGame.MoveRight, seq);
            var result = live.Tick();
            foreach (var e in result.Events)
            {
                fx.Events.Append(key, e);
            }

            if (result.Snapshot.Tick == 3)
            {
                fx.History.Save(key, result.Snapshot); // checkpoint at tick 3
            }

            expectedX[result.Snapshot.Tick] = X(live);
        }

        foreach (var target in new long[] { 1, 2, 4, 5, 6 })
        {
            var replay = fx.Service.ReplayTo(key, Game, target);

            Assert.Equal(RoomReplayOutcome.Replayed, replay.Outcome);
            Assert.Equal(target, replay.ReplayedToTick);
            Assert.Equal(target, replay.Room!.Snapshot().Tick); // clock positioned exactly at the target
            Assert.Equal(expectedX[target], X(replay.Room!));
        }
    }

    [Fact]
    public void ReplayTo_TargetBeyondLastEvent_PositionsClockAtTarget_StateUnchanged()
    {
        var fx = new ReplayFixture();
        var key = Key("tenant-a");

        // Checkpoint at tick 0; events only at ticks 1..3. The room never reached tick 5.
        var live = fx.NewMoveRightRoom();
        live.Join(Player);
        fx.History.Save(key, live.Snapshot());
        for (var seq = 1; seq <= 3; seq++)
        {
            live.TryEnqueue(Player, MoveRightGame.MoveRight, seq);
            foreach (var e in live.Tick().Events)
            {
                fx.Events.Append(key, e);
            }
        }

        // Replaying to tick 5 = state as of tick 3 (no commands on 4,5), but the clock must read 5 so a
        // resumed timeline advances from 6 cleanly.
        var replay = fx.Service.ReplayTo(key, Game, targetTick: 5);

        Assert.Equal(RoomReplayOutcome.Replayed, replay.Outcome);
        Assert.Equal(5L, replay.Room!.Snapshot().Tick);
        Assert.Equal(3, X(replay.Room!));
        Assert.Equal(3, replay.ReplayedEventCount);
    }

    [Fact]
    public void ReplayTo_StochasticGame_FromMidHistoryCheckpoint_MatchesLive()
    {
        var key = Key("tenant-a");
        var history = new InMemorySnapshotHistoryStore<RoomKey, RoomSnapshot>(s => s.Tick);
        var events = new InMemoryEventLog<RoomKey, RoomEvent>(e => e.Tick);

        // Live stochastic room: checkpoint mid-history at tick 3 (after the RNG has drawn three times).
        var liveRandom = new DeterministicRandomSource(seed: 4242);
        var live = new GameRoom(Arena, new StochasticGame(liveRandom), new FakeSimulationClock(), liveRandom);
        live.Join(Player);
        for (var seq = 1; seq <= 3; seq++)
        {
            live.TryEnqueue(Player, StochasticGame.Step, seq);
            foreach (var e in live.Tick().Events)
            {
                events.Append(key, e);
            }
        }

        history.Save(key, live.Snapshot()); // mid-history checkpoint: RngState captured after three draws
        for (var seq = 4; seq <= 6; seq++)
        {
            live.TryEnqueue(Player, StochasticGame.Step, seq);
            foreach (var e in live.Tick().Events)
            {
                events.Append(key, e);
            }
        }

        var livePos = StochasticGame.Decode(live.Project().Single().Payload);

        // The replay factory builds rooms with a DIFFERENT construction seed; only the restored RNG
        // state makes draws 4..6 line up. A seed-only header would diverge here.
        var service = new RoomReplayService(
            history,
            events,
            (roomId, _) =>
            {
                var rnd = new DeterministicRandomSource(seed: 1);
                return new GameRoom(roomId, new StochasticGame(rnd), new FakeSimulationClock(), rnd);
            },
            new TestTelemetrySink());

        var replay = service.ReplayTo(key, Game, targetTick: 6);

        Assert.Equal(RoomReplayOutcome.Replayed, replay.Outcome);
        Assert.Equal(livePos, StochasticGame.Decode(replay.Room!.Project().Single().Payload));
    }

    [Fact]
    public void ReplayTo_TargetBeforeEarliestCheckpoint_IsBeyondHorizon()
    {
        var fx = new ReplayFixture();
        var key = Key("tenant-a");

        // Only a checkpoint at tick 5 is retained (earlier history pruned past the horizon).
        fx.History.Save(key, new RoomSnapshot(5, new MoveRightGame().Serialize()));

        var replay = fx.Service.ReplayTo(key, Game, targetTick: 2);

        Assert.Equal(RoomReplayOutcome.BeyondHorizon, replay.Outcome);
        Assert.Null(replay.Room);
    }

    [Fact]
    public void ReplayTo_CorruptEvent_FailsExplicitly()
    {
        var fx = new ReplayFixture();
        var key = Key("tenant-a");
        fx.History.Save(key, new RoomSnapshot(0, BuildState(x: 0)));
        fx.Events.Append(key, new RoomEvent(1, Player, "Bogus")); // unrecognized command

        var replay = fx.Service.ReplayTo(key, Game, targetTick: 1);

        Assert.Equal(RoomReplayOutcome.Failed, replay.Outcome);
        Assert.Null(replay.Room);
    }

    [Fact]
    public void ReplayDoesNotCrossTenantBoundary()
    {
        var fx = new ReplayFixture();
        var keyA = Key("tenant-a");
        var keyB = Key("tenant-b");

        // Only tenant A has history for the same-named room.
        fx.History.Save(keyA, new RoomSnapshot(0, BuildState(x: 0)));
        fx.Events.Append(keyA, new RoomEvent(1, Player, MoveRightGame.MoveRight));

        // Replaying tenant B must never read tenant A's checkpoint/events.
        var replay = fx.Service.ReplayTo(keyB, Game, targetTick: 1);

        Assert.Equal(RoomReplayOutcome.BeyondHorizon, replay.Outcome);
        Assert.Null(replay.Room);
    }

    [Fact]
    public void ReplayTo_EmitsReplayTelemetry()
    {
        var fx = new ReplayFixture();
        var key = Key("tenant-a");
        fx.History.Save(key, new RoomSnapshot(0, BuildState(x: 0)));

        fx.Service.ReplayTo(key, Game, targetTick: 0);

        Assert.True(fx.Telemetry.HasEvent(TelemetryEvents.RoomReplayed));
        var replayed = fx.Telemetry.Events.Single(e => e.Name == TelemetryEvents.RoomReplayed);
        Assert.Equal("tenant-a", replayed.Fields!["tenantId"]);
        Assert.Equal("arena", replayed.Fields!["roomId"]);
        Assert.Equal("demo", replayed.Fields!["gameId"]);
        Assert.True(replayed.Fields!.ContainsKey("replayedToTick"));
        Assert.Equal(1, fx.Telemetry.CountIncrements(TelemetryMetrics.RoomReplayCount));
    }

    /// <summary>Opaque MoveRight state for a single player at position <paramref name="x"/>, via the real game.</summary>
    private static byte[] BuildState(int x)
    {
        var game = new MoveRightGame();
        game.Join(Player);
        for (var i = 0; i < x; i++)
        {
            game.Apply(Player, MoveRightGame.MoveRight);
        }

        return game.Serialize();
    }

    /// <summary>A stochastic game whose Step draws from the SHARED room random source — replay is only
    /// exact if that source resumes at the right draw position.</summary>
    private sealed class StochasticGame : IGameSimulation
    {
        public const string Step = "Step";

        private readonly IRandomSource _random;
        private readonly Dictionary<PlayerId, int> _pos = new();

        public StochasticGame(IRandomSource random) => _random = random;

        public void Join(PlayerId player) => _pos.TryAdd(player, 0);
        public bool HasPlayer(PlayerId player) => _pos.ContainsKey(player);
        public bool CanAccept(PlayerId player, string command) => command == Step;
        public void Apply(PlayerId player, string command) => _pos[player] += _random.Next(1000);

        public IReadOnlyList<EntitySnapshot> Project() => _pos
            .Select(p => new EntitySnapshot(new EntityId(p.Key.Value), p.Value, RelevanceKey.None, Encode(p.Value)))
            .ToList();

        public byte[] Serialize() =>
            JsonSerializer.SerializeToUtf8Bytes(_pos.ToDictionary(kv => kv.Key.Value, kv => kv.Value));

        public void Restore(byte[] state)
        {
            _pos.Clear();
            var restored = JsonSerializer.Deserialize<Dictionary<string, int>>(state) ?? new();
            foreach (var (player, p) in restored)
            {
                _pos[new PlayerId(player)] = p;
            }
        }

        private static byte[] Encode(int pos)
        {
            var payload = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(payload, pos);
            return payload;
        }

        public static int Decode(byte[] payload) => BinaryPrimitives.ReadInt32LittleEndian(payload);
    }
}
