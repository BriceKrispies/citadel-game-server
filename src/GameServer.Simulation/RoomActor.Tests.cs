using GameServer.Protocol;
using GameServer.Replication;
using GameServer.Simulation.Testing;
using Xunit;

namespace GameServer.Simulation;

/// <summary>
/// Tests the actor wrapper over a room. The actor adds no game behavior — it serializes delivery
/// and routes each <see cref="RoomMessage"/> to the wrapped <see cref="IGameRoom"/> — so the
/// central proof is equivalence: a sequence of messages drained through the actor must reach the
/// SAME authoritative result as the same calls made directly on a <see cref="GameRoom"/>. The
/// mailbox (arrival-order, applied-on-drain) and ask/tell reply wiring are tested alongside it.
/// </summary>
public sealed class RoomActorTests
{
    private static readonly PlayerId Player = new("p1");

    private static GameRoom NewRoom(IGameSimulation game) =>
        new(new RoomId("arena"), game, new FakeSimulationClock(), new DeterministicRandomSource());

    private static (RoomActor Actor, InlineRoomScheduler Driver) NewActor(IGameSimulation game)
    {
        var actor = new RoomActor(NewRoom(game));
        return (actor, new InlineRoomScheduler());
    }

    [Fact]
    public void Id_DelegatesToWrappedRoom()
    {
        var (actor, _) = NewActor(new FakeGame());

        Assert.Equal(new RoomId("arena"), actor.Id);
    }

    [Fact]
    public async Task EnqueueCommand_FromNonMember_RepliesRejectedNotJoined()
    {
        var (actor, driver) = NewActor(new FakeGame());

        var ask = new EnqueueCommand(Player, "Go", 1);
        actor.Post(ask);
        driver.Drain(actor);

        Assert.Equal(CommandAdmission.RejectedNotJoined, await ask.Reply);
    }

    [Fact]
    public async Task PostedMessage_IsNotAppliedUntilDrained()
    {
        var game = new FakeGame();
        var (actor, driver) = NewActor(game);
        actor.Post(new JoinPlayer(Player));
        driver.Drain(actor);

        var command = new EnqueueCommand(Player, "Go", 1);
        actor.Post(command);
        var tick = new AdvanceTick();
        actor.Post(tick);

        // Queued in the mailbox, nothing applied yet.
        Assert.Equal(2, actor.PendingCount);
        Assert.Empty(game.Applied);

        Assert.Equal(2, driver.Drain(actor));
        Assert.Equal(0, actor.PendingCount);
        Assert.Single(game.Applied); // applied on the tick that drained after the enqueue
        Assert.Equal(CommandAdmission.Accepted, await command.Reply);
    }

    [Fact]
    public async Task Messages_AreAppliedInArrivalOrder()
    {
        var game = new FakeGame();
        var (actor, driver) = NewActor(game);
        actor.Post(new JoinPlayer(Player));

        var first = new EnqueueCommand(Player, "Go", 1);
        var second = new EnqueueCommand(Player, "Go", 2);
        actor.Post(first);
        actor.Post(second);
        var tick = new AdvanceTick();
        actor.Post(tick);
        driver.Drain(actor);

        Assert.Equal(CommandAdmission.Accepted, await first.Reply);
        Assert.Equal(CommandAdmission.Accepted, await second.Reply);
        var result = await tick.Reply;
        Assert.Equal(2, result.Events.Count);
        Assert.Equal(1L, result.Snapshot.Tick);
        Assert.All(result.Events, e => Assert.Equal(1L, e.Tick));
    }

    [Fact]
    public async Task StaleSequence_RepliesRejected_ThroughTheMailbox()
    {
        var (actor, driver) = NewActor(new FakeGame());
        actor.Post(new JoinPlayer(Player));

        var accepted = new EnqueueCommand(Player, "Go", 5);
        var stale = new EnqueueCommand(Player, "Go", 5);
        actor.Post(accepted);
        actor.Post(stale);
        driver.Drain(actor);

        Assert.Equal(CommandAdmission.Accepted, await accepted.Reply);
        Assert.Equal(CommandAdmission.RejectedStaleSequence, await stale.Reply);
    }

    [Fact]
    public async Task QueryAsks_ReplyWithRoomState()
    {
        var (actor, driver) = NewActor(new FakeGame());
        actor.Post(new JoinPlayer(Player));

        var canJoin = new QueryCanJoin(new PlayerId("p2"));
        var hasPlayer = new QueryHasPlayer(Player);
        var depth = new QueryQueueDepth();
        var project = new ProjectEntities();
        actor.Post(canJoin);
        actor.Post(hasPlayer);
        actor.Post(depth);
        actor.Post(project);
        driver.Drain(actor);

        Assert.True(await canJoin.Reply);
        Assert.True(await hasPlayer.Reply);
        Assert.Equal(0, await depth.Reply);
        Assert.Equal(new[] { "p1" }, (await project.Reply).Select(e => e.Id.Value).ToArray());
    }

    [Fact]
    public async Task CaptureSnapshot_RepliesWithReplayHeader()
    {
        var actor = new RoomActor(new GameRoom(
            new RoomId("arena"), new FakeGame(schemaVersion: 7), new FakeSimulationClock(), new DeterministicRandomSource(seed: 4242)));
        var driver = new InlineRoomScheduler();

        var ask = new CaptureSnapshot();
        actor.Post(ask);
        driver.Drain(actor);

        var snapshot = await ask.Reply;
        Assert.Equal(4242, snapshot.Seed);
        Assert.Equal(7, snapshot.GameSchemaVersion);
    }

    [Fact]
    public async Task ReplayRecoveredEvent_ForAbsentPlayer_JoinsThenApplies()
    {
        var game = new FakeGame(legal: "Go");
        var (actor, driver) = NewActor(game);

        var ask = new ReplayRecoveredEvent(new RoomEvent(1, Player, "Go"));
        actor.Post(ask);
        var hasPlayer = new QueryHasPlayer(Player);
        actor.Post(hasPlayer);
        driver.Drain(actor);

        Assert.True(await ask.Reply);
        Assert.True(await hasPlayer.Reply);
        Assert.Single(game.Applied);
    }

    [Fact]
    public async Task RestoreRoom_ResetsSequenceGating_ThroughTheMailbox()
    {
        var game = new FakeGame();
        var (actor, driver) = NewActor(game);
        actor.Post(new JoinPlayer(Player));
        actor.Post(new EnqueueCommand(Player, "Go", 5));
        actor.Post(new AdvanceTick());
        actor.Post(new RestoreRoom(new RoomSnapshot(0, game.Serialize())));

        var reused = new EnqueueCommand(Player, "Go", 5);
        actor.Post(reused);
        driver.Drain(actor);

        // Gating restarted at the recovered baseline: a previously-used sequence is accepted again.
        Assert.Equal(CommandAdmission.Accepted, await reused.Reply);
    }

    [Fact]
    public async Task AskHandlerThatThrows_FaultsTheReply_AndActorSurvives()
    {
        // A game whose Apply throws must not hang the awaiting caller or kill the actor: the ask's
        // reply faults, and the next message still processes.
        var (actor, driver) = NewActor(new ThrowingGame());
        actor.Post(new JoinPlayer(Player));
        actor.Post(new EnqueueCommand(Player, "Boom", 1)); // queued; ThrowingGame.Apply throws on tick

        var faulting = new AdvanceTick();
        actor.Post(faulting);
        var after = new QueryHasPlayer(Player);
        actor.Post(after);
        driver.Drain(actor);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await faulting.Reply);
        Assert.True(await after.Reply); // the actor kept processing after the faulted ask
    }

    [Fact]
    public void UnknownMessageType_FaultsAsAsk_WithoutThrowingToDriver()
    {
        // The default dispatch arm makes an unhandled message explicit. For an ask it becomes a
        // faulted reply (caller observes it); the driver's drain is not aborted.
        var (actor, driver) = NewActor(new FakeGame());
        var unknown = new UnknownAsk();
        actor.Post(unknown);

        Assert.Equal(1, driver.Drain(actor)); // processed (and faulted), not thrown to the driver
        Assert.True(unknown.Reply.IsFaulted);
    }

    [Fact]
    public void DrainingAnEmptyMailbox_ProcessesNothing()
    {
        var (actor, driver) = NewActor(new FakeGame());

        Assert.Equal(0, driver.Drain(actor));
        Assert.False(actor.TryProcessOne());
    }

    [Fact]
    public async Task ActorPath_IsEquivalentToDirectRoomUse()
    {
        // The behavior-preserving proof: the same join + commands + ticks, once driven through the
        // actor's mailbox and once called directly on a GameRoom, reach identical projected state.
        var roomId = new RoomId("arena");

        var direct = new GameRoom(roomId, new MoveRightGame(), new FakeSimulationClock(), new DeterministicRandomSource());
        direct.Join(Player);
        for (var seq = 1; seq <= 3; seq++)
        {
            Assert.Equal(CommandAdmission.Accepted, direct.TryEnqueue(Player, MoveRightGame.MoveRight, seq));
        }

        direct.Tick();
        var directX = MoveRightGame.DecodeX(direct.Project().Single().Payload);

        var actor = new RoomActor(new GameRoom(roomId, new MoveRightGame(), new FakeSimulationClock(), new DeterministicRandomSource()));
        var driver = new InlineRoomScheduler();
        actor.Post(new JoinPlayer(Player));
        for (var seq = 1; seq <= 3; seq++)
        {
            actor.Post(new EnqueueCommand(Player, MoveRightGame.MoveRight, seq));
        }

        actor.Post(new AdvanceTick());
        var projected = new ProjectEntities();
        actor.Post(projected);
        driver.Drain(actor);

        var actorX = MoveRightGame.DecodeX((await projected.Reply).Single().Payload);
        Assert.Equal(directX, actorX);
    }

    /// <summary>A message type the actor has no handler for — exercises the default dispatch arm.</summary>
    private sealed record UnknownAsk : RoomAsk<bool>;

    /// <summary>A game whose command application throws, to drive the ask-fault path.</summary>
    private sealed class ThrowingGame : IGameSimulation
    {
        private readonly HashSet<PlayerId> _players = new();

        public void Join(PlayerId player) => _players.Add(player);
        public bool HasPlayer(PlayerId player) => _players.Contains(player);
        public bool CanAccept(PlayerId player, string command) => true;
        public void Apply(PlayerId player, string command) => throw new InvalidOperationException("boom");
        public IReadOnlyList<EntitySnapshot> Project() => Array.Empty<EntitySnapshot>();
        public byte[] Serialize() => Array.Empty<byte>();
        public void Restore(byte[] state) { }
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
