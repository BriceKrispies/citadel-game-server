using GameServer.Persistence;
using GameServer.Protocol;
using GameServer.Replication;
using GameServer.Simulation.Testing;
using Xunit;

namespace GameServer.Simulation;

/// <summary>
/// Tests the generic room host in isolation from any real game, using a recording
/// fake <see cref="IGameSimulation"/>. The host owns admission (membership, sequence,
/// legality), the tick loop (apply-in-order + events + clock), projection delegation,
/// and restore. Game-specific state effects are tested against the games themselves.
/// </summary>
public sealed class GameRoomTests
{
    private static readonly PlayerId Player = new("p1");

    private static GameRoom NewRoom(FakeGame game) =>
        new(new RoomId("arena"), game, new FakeSimulationClock(), new DeterministicRandomSource());

    [Fact]
    public void Command_FromNonMember_IsRejected()
    {
        var room = NewRoom(new FakeGame());

        Assert.Equal(CommandAdmission.RejectedNotJoined, room.TryEnqueue(Player, "Go", 1));
    }

    [Fact]
    public void UnknownCommand_IsRejectedAsInvalid()
    {
        var room = NewRoom(new FakeGame(legal: "Go"));
        room.Join(Player);

        Assert.Equal(CommandAdmission.RejectedInvalidCommand, room.TryEnqueue(Player, "Nope", 1));
    }

    [Fact]
    public void StaleOrDuplicateSequence_IsRejected()
    {
        var room = NewRoom(new FakeGame());
        room.Join(Player);

        Assert.Equal(CommandAdmission.Accepted, room.TryEnqueue(Player, "Go", 5));
        Assert.Equal(CommandAdmission.RejectedStaleSequence, room.TryEnqueue(Player, "Go", 5));
        Assert.Equal(CommandAdmission.RejectedStaleSequence, room.TryEnqueue(Player, "Go", 4));
        Assert.Equal(CommandAdmission.Accepted, room.TryEnqueue(Player, "Go", 6));
    }

    [Fact]
    public void TryEnqueue_BeyondQueueCapacity_ShedsAsOverloaded_ThenAcceptsAfterTickDrains()
    {
        var game = new FakeGame();
        var room = new GameRoom(
            new RoomId("arena"), game, new FakeSimulationClock(), new DeterministicRandomSource(), maxQueueDepth: 2);
        room.Join(Player);

        Assert.Equal(CommandAdmission.Accepted, room.TryEnqueue(Player, "Go", 1));
        Assert.Equal(CommandAdmission.Accepted, room.TryEnqueue(Player, "Go", 2));
        Assert.Equal(2, room.QueueDepth);

        // A third otherwise-valid command overflows the bounded queue: shed explicitly,
        // not enqueued, and its sequence is NOT recorded.
        Assert.Equal(CommandAdmission.RejectedOverloaded, room.TryEnqueue(Player, "Go", 3));
        Assert.Equal(2, room.QueueDepth);

        // Draining via a tick restores capacity, and the shed sequence is accepted on retry.
        room.Tick();
        Assert.Equal(0, room.QueueDepth);
        Assert.Equal(CommandAdmission.Accepted, room.TryEnqueue(Player, "Go", 3));
    }

    [Fact]
    public void Construction_WithNonPositiveQueueDepth_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GameRoom(new RoomId("arena"), new FakeGame(), new FakeSimulationClock(), new DeterministicRandomSource(), maxQueueDepth: 0));
    }

    [Fact]
    public void QueuedCommand_IsNotAppliedUntilTick()
    {
        var game = new FakeGame();
        var room = NewRoom(game);
        room.Join(Player);

        room.TryEnqueue(Player, "Go", 1);
        Assert.Empty(game.Applied); // queued, not yet applied

        room.Tick();
        Assert.Single(game.Applied);
    }

    [Fact]
    public void Tick_AppliesAcceptedCommandsInOrder_EmitsEvents_AndAdvancesClock()
    {
        var game = new FakeGame();
        var room = NewRoom(game);
        room.Join(Player);
        room.TryEnqueue(Player, "Go", 1);
        room.TryEnqueue(Player, "Go", 2);

        var result = room.Tick();

        Assert.Equal(2, game.Applied.Count);
        Assert.Equal(2, result.Events.Count);
        Assert.Equal(1L, result.Snapshot.Tick);
        Assert.All(result.Events, e => Assert.Equal(1L, e.Tick));
        Assert.All(result.Events, e => Assert.Equal("Go", e.Command));
    }

    [Fact]
    public void Project_DelegatesToGame()
    {
        var room = NewRoom(new FakeGame());
        room.Join(Player);

        Assert.Equal(new[] { "p1" }, room.Project().Select(e => e.Id.Value).ToArray());
    }

    [Fact]
    public void RestoreFrom_ResetsSequenceGating()
    {
        var game = new FakeGame();
        var room = NewRoom(game);
        room.Join(Player);
        room.TryEnqueue(Player, "Go", 5);
        room.Tick();

        room.RestoreFrom(new RoomSnapshot(0, game.Serialize()));

        // Gating restarts from the recovered baseline: a previously-used sequence is accepted again.
        Assert.Equal(CommandAdmission.Accepted, room.TryEnqueue(Player, "Go", 5));
    }

    [Fact]
    public void Snapshot_CapturesReplayHeader_SeedAndSchemaVersion()
    {
        var game = new FakeGame(schemaVersion: 7);
        var room = new GameRoom(
            new RoomId("arena"), game, new FakeSimulationClock(), new DeterministicRandomSource(seed: 4242));

        var snapshot = room.Snapshot();

        Assert.Equal(4242, snapshot.Seed);
        Assert.Equal(7, snapshot.GameSchemaVersion);
    }

    [Fact]
    public void RestoreFrom_ReseedsRandomSource_FromCapturedSeed()
    {
        // A fresh source on a different construction seed, restored from a snapshot whose header
        // carries a known seed, must reproduce that seed's stream — the cross-process replay
        // invariant in the small.
        var random = new DeterministicRandomSource(seed: 1);
        var room = new GameRoom(new RoomId("arena"), new FakeGame(), new FakeSimulationClock(), random);

        room.RestoreFrom(new RoomSnapshot(0, Array.Empty<byte>(), Seed: 999, GameSchemaVersion: 1));

        Assert.Equal(999, random.Seed);
        Assert.Equal(new DeterministicRandomSource(seed: 999).Next(1_000_000), random.Next(1_000_000));
    }

    [Fact]
    public void RestoreFrom_LegacyHeaderlessSnapshot_LeavesConstructionSeed()
    {
        // A snapshot persisted before the replay header existed deserializes with Seed == 0; restore
        // must NOT reseed to 0 (back-compat: the source keeps its construction seed).
        var random = new DeterministicRandomSource(seed: 5);
        var room = new GameRoom(new RoomId("arena"), new FakeGame(), new FakeSimulationClock(), random);

        room.RestoreFrom(new RoomSnapshot(0, Array.Empty<byte>())); // no header

        Assert.Equal(5, random.Seed);
    }

    [Fact]
    public void DeterministicReplayWithSeed_FreshRoom_FromSnapshotPlusEvents_YieldsIdenticalState()
    {
        // #6 (hermetic): a fresh room + the captured snapshot header (seed + schema) + the recorded
        // post-snapshot events must reach the SAME entity state, with NO wall-clock involved. This is
        // the in-memory proof of the cross-process replay invariant; the Postgres-gated integration
        // test exercises the same path through a durable store.
        var snapshots = new InMemorySnapshotStore<RoomId, RoomSnapshot>();
        var events = new InMemoryEventLog<RoomId, RoomEvent>(e => e.Tick);
        var roomId = new RoomId("arena");

        // --- original room: take an empty baseline snapshot, then run moves recorded as events ---
        var original = new GameRoom(roomId, new MoveRightGame(), new FakeSimulationClock(), new SeededRandomSource(seed: 7));
        original.Join(Player);
        snapshots.Save(roomId, original.Snapshot()); // baseline at tick 0 (header captured)
        for (var seq = 1; seq <= 3; seq++)
        {
            original.TryEnqueue(Player, MoveRightGame.MoveRight, seq);
        }

        foreach (var produced in original.Tick().Events)
        {
            events.Append(roomId, produced);
        }

        var originalX = MoveRightGame.DecodeX(original.Project().Single().Payload);

        // --- fresh room (simulated new process): restore from the header, replay the events ---
        Assert.True(snapshots.TryGetLatest(roomId, out var header));
        var replayed = new GameRoom(roomId, new MoveRightGame(), new FakeSimulationClock(), new SeededRandomSource(seed: 1));
        replayed.RestoreFrom(header); // re-seeds from the captured header, not the construction seed
        foreach (var recovered in events.Read(roomId))
        {
            Assert.True(replayed.ApplyRecoveredEvent(recovered));
        }

        var replayedX = MoveRightGame.DecodeX(replayed.Project().Single().Payload);
        Assert.Equal(originalX, replayedX);
        Assert.Equal(7, header.Seed); // the header carried the original room's seed
    }

    [Fact]
    public void DeterministicReplay_StochasticGame_FreshRoomReseededFromHeader_YieldsIdenticalState()
    {
        // ADVERSARIAL (replay determinism): unlike MoveRight/GridWalk, this game DRAWS from the room's
        // IRandomSource on every applied command, so its post-snapshot trajectory depends entirely on
        // the RNG sequence. The cross-process replay invariant only holds if the captured seed re-seeds
        // a fresh source into the IDENTICAL draw sequence. The game shares the room's random instance
        // (the room exposes it via .Random), exactly how a real stochastic game would be wired.
        var roomId = new RoomId("arena");
        var snapshots = new InMemorySnapshotStore<RoomId, RoomSnapshot>();
        var events = new InMemoryEventLog<RoomId, RoomEvent>(e => e.Tick);

        // --- original room: empty baseline snapshot, then a run of stochastic steps recorded as events
        var originalRandom = new SeededRandomSource(seed: 31337);
        var originalGame = new StochasticWalkGame(originalRandom);
        var original = new GameRoom(roomId, originalGame, new FakeSimulationClock(), originalRandom);
        original.Join(Player);
        snapshots.Save(roomId, original.Snapshot()); // baseline at tick 0, header captures seed 31337
        for (var seq = 1; seq <= 6; seq++)
        {
            original.TryEnqueue(Player, StochasticWalkGame.Step, seq);
            foreach (var produced in original.Tick().Events)
            {
                events.Append(roomId, produced);
            }
        }

        var originalPos = StochasticWalkGame.Decode(original.Project().Single().Payload);

        // --- fresh room (new process): a DIFFERENT construction seed, restored from the header. ---
        Assert.True(snapshots.TryGetLatest(roomId, out var header));
        Assert.Equal(31337, header.Seed);
        var replayRandom = new SeededRandomSource(seed: 1); // deliberately wrong construction seed
        var replayGame = new StochasticWalkGame(replayRandom);
        var replayed = new GameRoom(roomId, replayGame, new FakeSimulationClock(), replayRandom);
        replayed.RestoreFrom(header); // re-seeds the shared source to 31337
        foreach (var recovered in events.Read(roomId))
        {
            Assert.True(replayed.ApplyRecoveredEvent(recovered));
        }

        var replayedPos = StochasticWalkGame.Decode(replayed.Project().Single().Payload);
        Assert.Equal(originalPos, replayedPos); // identical despite the stochastic rule
    }

    [Fact]
    public void DeterministicReplay_StochasticGame_WithoutReseed_Diverges_ProvingSeedIsLoadBearing()
    {
        // The companion to the above: prove the seed is genuinely load-bearing (not decorative). A
        // legacy header-less snapshot (Seed == 0) leaves the fresh source on its OWN construction seed
        // — the DOCUMENTED fallback. With a stochastic game and a different construction seed, that
        // fallback necessarily DIVERGES; replay is only deterministic when the seed is present. This
        // locks in that a missing seed is an explicit (different-but-defined) outcome, never silent
        // "it happened to match".
        var roomId = new RoomId("arena");
        var snapshots = new InMemorySnapshotStore<RoomId, RoomSnapshot>();
        var events = new InMemoryEventLog<RoomId, RoomEvent>(e => e.Tick);

        var originalRandom = new SeededRandomSource(seed: 31337);
        var originalGame = new StochasticWalkGame(originalRandom);
        var original = new GameRoom(roomId, originalGame, new FakeSimulationClock(), originalRandom);
        original.Join(Player);
        snapshots.Save(roomId, original.Snapshot());
        for (var seq = 1; seq <= 6; seq++)
        {
            original.TryEnqueue(Player, StochasticWalkGame.Step, seq);
            foreach (var produced in original.Tick().Events)
            {
                events.Append(roomId, produced);
            }
        }

        var originalPos = StochasticWalkGame.Decode(original.Project().Single().Payload);

        Assert.True(snapshots.TryGetLatest(roomId, out var header));
        // Strip the header to simulate a legacy/seedless snapshot (Seed == 0 → no reseed on restore).
        var seedless = new RoomSnapshot(header.Tick, header.State);

        var replayRandom = new SeededRandomSource(seed: 1);
        var replayGame = new StochasticWalkGame(replayRandom);
        var replayed = new GameRoom(roomId, replayGame, new FakeSimulationClock(), replayRandom);
        replayed.RestoreFrom(seedless); // Seed == 0: keeps construction seed 1, does NOT reseed to 0
        foreach (var recovered in events.Read(roomId))
        {
            Assert.True(replayed.ApplyRecoveredEvent(recovered));
        }

        var replayedPos = StochasticWalkGame.Decode(replayed.Project().Single().Payload);
        Assert.NotEqual(originalPos, replayedPos); // seedless replay diverges: the seed is load-bearing
    }

    /// <summary>
    /// A stochastic reference game for replay testing: each player has an integer position that a
    /// "Step" command advances by a random amount drawn from the SHARED room random source. Because
    /// the trajectory depends on the RNG, replay is only reproducible if the source is re-seeded to the
    /// original's seed — exactly the cross-process invariant the snapshot header exists to guarantee.
    /// </summary>
    private sealed class StochasticWalkGame : IGameSimulation
    {
        public const string Step = "Step";

        private readonly IRandomSource _random;
        private readonly Dictionary<PlayerId, int> _pos = new();

        public StochasticWalkGame(IRandomSource random) => _random = random;

        public void Join(PlayerId player) => _pos.TryAdd(player, 0);
        public bool HasPlayer(PlayerId player) => _pos.ContainsKey(player);
        public bool CanAccept(PlayerId player, string command) => command == Step;

        public void Apply(PlayerId player, string command) => _pos[player] += _random.Next(1000);

        public IReadOnlyList<EntitySnapshot> Project() => _pos
            .Select(p => new EntitySnapshot(new EntityId(p.Key.Value), p.Value, RelevanceKey.None, Encode(p.Value)))
            .ToList();

        public byte[] Serialize() =>
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(_pos.ToDictionary(kv => kv.Key.Value, kv => kv.Value));

        public void Restore(byte[] state)
        {
            _pos.Clear();
            var restored = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(state) ?? new();
            foreach (var (player, p) in restored)
            {
                _pos[new PlayerId(player)] = p;
            }
        }

        public static byte[] Encode(int pos)
        {
            var payload = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(payload, pos);
            return payload;
        }

        public static int Decode(byte[] payload) => System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload);
    }

    /// <summary>Records applied commands; accepts a single legal command. No real state.</summary>
    private sealed class FakeGame : IGameSimulation
    {
        private readonly string _legal;
        private readonly int _schemaVersion;
        private readonly HashSet<PlayerId> _players = new();

        public FakeGame(string legal = "Go", int schemaVersion = 1)
        {
            _legal = legal;
            _schemaVersion = schemaVersion;
        }

        public int SchemaVersion => _schemaVersion;

        public List<(PlayerId Player, string Command)> Applied { get; } = new();

        public void Join(PlayerId player) => _players.Add(player);
        public bool HasPlayer(PlayerId player) => _players.Contains(player);
        public bool CanAccept(PlayerId player, string command) => command == _legal;
        public void Apply(PlayerId player, string command) => Applied.Add((player, command));

        public IReadOnlyList<EntitySnapshot> Project() => _players
            .Select(p => new EntitySnapshot(new EntityId(p.Value), Applied.Count, RelevanceKey.None, Array.Empty<byte>()))
            .ToList();

        public byte[] Serialize() => Array.Empty<byte>();
        public void Restore(byte[] state) { }
    }
}
