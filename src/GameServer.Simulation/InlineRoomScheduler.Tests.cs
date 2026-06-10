using GameServer.Protocol;
using GameServer.Replication;
using GameServer.Simulation.Testing;
using Xunit;

namespace GameServer.Simulation;

/// <summary>
/// Tests the deterministic inline driver: it drains a room actor's mailbox to idle synchronously
/// and reports how much work it did, with no threads and no wall-clock time. This is the pump that
/// lets tests and replay drive the actor path by hand, the actor-model analogue of a manually
/// advanced clock.
/// </summary>
public sealed class InlineRoomSchedulerTests
{
    private static readonly PlayerId Player = new("p1");

    private static RoomActor NewActor() =>
        new(new GameRoom(new RoomId("arena"), new CountingGame(), new FakeSimulationClock(), new DeterministicRandomSource()));

    [Fact]
    public void Drain_AppliesEveryQueuedMessage_AndReturnsTheCount()
    {
        var actor = NewActor();
        var driver = new InlineRoomScheduler();
        actor.Post(new JoinPlayer(Player));
        actor.Post(new EnqueueCommand(Player, "Go", 1));
        actor.Post(new AdvanceTick());

        Assert.Equal(3, driver.Drain(actor));
        Assert.Equal(0, actor.PendingCount);
    }

    [Fact]
    public void Drain_OnEmptyMailbox_IsZeroAndIdempotent()
    {
        var driver = new InlineRoomScheduler();
        var actor = NewActor();

        Assert.Equal(0, driver.Drain(actor));
        Assert.Equal(0, driver.Drain(actor));
    }

    [Fact]
    public void Drain_OverManyActors_DrivesEachToIdle()
    {
        var driver = new InlineRoomScheduler();
        var actors = new[] { NewActor(), NewActor(), NewActor() };
        foreach (var actor in actors)
        {
            actor.Post(new JoinPlayer(Player));
            actor.Post(new AdvanceTick());
        }

        Assert.Equal(6, driver.Drain(actors));
        Assert.All(actors, a => Assert.Equal(0, a.PendingCount));
    }

    [Fact]
    public void Drain_NullActor_Throws()
    {
        var driver = new InlineRoomScheduler();

        Assert.Throws<ArgumentNullException>(() => driver.Drain((RoomActor)null!));
        Assert.Throws<ArgumentNullException>(() => driver.Drain((IEnumerable<RoomActor>)null!));
    }

    /// <summary>A trivial game that admits any player and accepts any command.</summary>
    private sealed class CountingGame : IGameSimulation
    {
        private readonly HashSet<PlayerId> _players = new();

        public void Join(PlayerId player) => _players.Add(player);
        public bool HasPlayer(PlayerId player) => _players.Contains(player);
        public bool CanAccept(PlayerId player, string command) => true;
        public void Apply(PlayerId player, string command) { }
        public IReadOnlyList<EntitySnapshot> Project() => Array.Empty<EntitySnapshot>();
        public byte[] Serialize() => Array.Empty<byte>();
        public void Restore(byte[] state) { }
    }
}
