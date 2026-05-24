using GameServer.Observability;
using GameServer.Persistence;
using GameServer.Protocol;
using GameServer.Routing;
using GameServer.Simulation;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// Gap #16 (durability) — recovery has nothing durable to recover from. The (well-tested)
/// <see cref="RoomRecoveryService"/> rebuilds a room from a stored snapshot + event replay, but the
/// only snapshot/event stores are in-memory, living in one process's heap. A restart or node
/// failure therefore loses every live room. This scenario runs a room and checkpoints it, then
/// simulates a restart by reading that checkpoint from a DURABLE store on a fresh instance and
/// recovering the room. It FAILS today via the unimplemented <see cref="FileSnapshotStore{TKey,TSnapshot}"/>
/// seam (the checkpoint cannot be persisted), and turns green once a restart-surviving store backs
/// the same <see cref="ISnapshotStore{TKey,TSnapshot}"/> contract.
/// </summary>
public sealed class DurableRestartScenario
{
    private readonly ITestOutputHelper _output;

    public DurableRestartScenario(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task RoomSurvivesARestart_ViaADurableCheckpoint()
    {
        var dir = Path.Combine(Path.GetTempPath(), "citadel-durable-" + Guid.NewGuid().ToString("n"));

        // --- "Node 1": run a real room and produce an authoritative checkpoint ---
        var node1 = new IntegrationHarness(_ => new MoveRightGame());
        await node1.RunClientAsync("tenant-a", "arena", "p1", "demo", Enumerable.Repeat(MoveRightGame.MoveRight, 5).ToArray());
        var key = node1.Key("tenant-a", "arena");
        await node1.Server.TickRoom(key); // applies the 5 moves and saves the snapshot
        Assert.True(node1.Snapshots.TryGetLatest(key, out var checkpoint));

        // The checkpoint must outlive the process. Persist it to a durable store...
        IDurableSnapshotStore<RoomKey, RoomSnapshot> durable = new FileSnapshotStore<RoomKey, RoomSnapshot>(dir);
        durable.Save(key, checkpoint); // RED: durable persistence is not implemented yet.

        // --- "Node 2": a fresh instance after a restart reads the same durable location ---
        var durableAfterRestart = new FileSnapshotStore<RoomKey, RoomSnapshot>(dir);
        var recovery = new RoomRecoveryService(
            durableAfterRestart,
            new InMemoryEventLog<RoomKey, RoomEvent>(e => e.Tick),
            (roomId, _) => new GameRoom(roomId, new MoveRightGame(), new LogicalSimulationClock(), new SeededRandomSource()),
            new AggregatingTelemetrySink());

        var result = recovery.Restore(key, new GameId("demo"), MissingSnapshotPolicy.Fail);

        Assert.Equal(RoomRecoveryOutcome.RestoredFromSnapshot, result.Outcome);
        var restoredX = MoveRightGame.DecodeX(result.Room!.Project().Single(e => e.Id.Value == "p1").Payload);
        _output.WriteLine($"recovered p1.x after restart = {restoredX}");
        Assert.Equal(5, restoredX);
    }
}
