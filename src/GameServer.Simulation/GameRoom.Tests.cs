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

    /// <summary>Records applied commands; accepts a single legal command. No real state.</summary>
    private sealed class FakeGame : IGameSimulation
    {
        private readonly string _legal;
        private readonly HashSet<PlayerId> _players = new();

        public FakeGame(string legal = "Go") => _legal = legal;

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
