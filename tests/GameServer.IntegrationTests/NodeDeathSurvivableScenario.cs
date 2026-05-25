using GameServer.Cluster.Redis;
using GameServer.Observability;
using GameServer.Persistence;
using GameServer.Protocol;
using GameServer.Routing;
using GameServer.Simulation;
using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// Wave 6 — node-death survivability. A node that owns a live room dies mid-match; the room must NOT be
/// lost: another node takes ownership and recovers the authoritative state, and a client can reconnect to
/// the new owner. This composes the Wave-4 cluster (a real leased <see cref="RedisRoomDirectory"/> +
/// capacity-aware <see cref="RedisRoomPlacement"/>) with the durable-recovery path
/// (<see cref="RoomRecoveryService"/> over a shared, restart-surviving snapshot store + event log).
/// <para>
/// Death is modelled exactly as the fencing scenario models a partition: the owner stops renewing its
/// SHORT lease, so its owner key expires and the room becomes reclaimable — the same effect as a crashed
/// process that can no longer run its <c>RoomLeaseRenewalService</c>. The surviving node then legitimately
/// claims the room via placement and rebuilds it from the durable artifacts the dead node left behind. The
/// recovered tick/state must equal what node-A had — the match resumes, it is not silently reset.
/// </para>
/// Redis-gated: SKIPS (not passes) when no container engine is available, keeping the suite honest.
/// </summary>
/// <remarks>Integration scenario: requires a container engine (Podman/Docker); kept out of the fast unit loop.</remarks>
public sealed class NodeDeathSurvivableScenario
{
    private static readonly NodeId NodeA = new("https://node-a:5000");
    private static readonly NodeId NodeB = new("https://node-b:5000");

    private readonly ITestOutputHelper _output;

    public NodeDeathSurvivableScenario(ITestOutputHelper output) => _output = output;

    [SkippableFact]
    public async Task NodeDeathSurvivable()
    {
        RedisContainer redis;
        try
        {
            redis = new RedisBuilder("redis:7-alpine").Build();
            await redis.StartAsync();
        }
        catch (Exception ex)
        {
            throw new SkipException($"Docker/Redis not available in this environment: {ex.Message}");
        }

        await using (redis)
        {
            using var mux = await ConnectionMultiplexer.ConnectAsync(redis.GetConnectionString());
            // SHORT lease so "node death" can be modelled by simply not renewing (no live process to renew).
            var options = new RedisClusterOptions(KeyPrefix: $"node-death-{Guid.NewGuid():n}", LeaseMs: 400);
            var room = new RoomKey(new TenantId("tenant-a"), new RoomId("arena"));

            // ONE shared durable snapshot store + event log = the restart-surviving runtime data both nodes
            // read (a real fleet shares Postgres/object storage; here a temp-dir file store + a shared log
            // stand in, exactly as ClusterRoutingHostScenario shares one directory). The dead node's
            // checkpoint/events outlive it here, so the survivor has something to recover from.
            var snapshotDir = Path.Combine(Path.GetTempPath(), "citadel-nodedeath-" + Guid.NewGuid().ToString("n"));
            IDurableSnapshotStore<RoomKey, RoomSnapshot> sharedSnapshots = new FileSnapshotStore<RoomKey, RoomSnapshot>(snapshotDir);
            var sharedEvents = new InMemoryEventLog<RoomKey, RoomEvent>(e => e.Tick);

            // ---- Node-A owns the room and runs a match (5 authoritative moves), checkpointing durably. ----
            var directoryA = new RedisRoomDirectory(mux, options);
            var placementA = new RedisRoomPlacement(mux, new[] { NodeA, NodeB }, maxRoomsPerNode: 100, options);
            Assert.Equal(NodeA, placementA.Place(room).Owner);

            var roomA = new GameRoom(room.RoomId, new MoveRightGame(), new LogicalSimulationClock(), new SeededRandomSource());
            roomA.Join(new PlayerId("p1"));
            for (long seq = 1; seq <= 5; seq++)
            {
                roomA.TryEnqueue(new PlayerId("p1"), MoveRightGame.MoveRight, seq);
            }

            var resultA = roomA.Tick(); // applies the 5 moves
            sharedSnapshots.Save(room, resultA.Snapshot);
            foreach (var ev in resultA.Events)
            {
                sharedEvents.Append(room, ev);
            }

            var preDeathX = MoveRightGame.DecodeX(roomA.Project().Single(e => e.Id.Value == "p1").Payload);
            Assert.Equal(5, preDeathX);

            // ---- Node-A "dies": it stops renewing. After the lease lapses its owner key expires. ----
            await Task.Delay(options.LeaseMs + 250);

            // ---- Node-B survives: it legitimately claims the now-reclaimable room via placement... ----
            var directoryB = new RedisRoomDirectory(mux, options);
            var placementB = new RedisRoomPlacement(mux, new[] { NodeB, NodeA }, maxRoomsPerNode: 100, options);
            var reclaimed = placementB.Place(room);
            Assert.True(reclaimed.IsPlaced);
            Assert.Equal(NodeB, reclaimed.Owner); // the dead node's room migrated to the survivor

            // ...and rebuilds the authoritative room from the dead node's durable checkpoint + events.
            var recovery = new RoomRecoveryService(
                sharedSnapshots,
                sharedEvents,
                (roomId, _) => new GameRoom(roomId, new MoveRightGame(), new LogicalSimulationClock(), new SeededRandomSource()),
                new AggregatingTelemetrySink());
            var restore = recovery.Restore(room, new GameId("demo"), MissingSnapshotPolicy.Fail);

            Assert.Equal(RoomRecoveryOutcome.RestoredFromSnapshot, restore.Outcome);
            var recoveredX = MoveRightGame.DecodeX(restore.Room!.Project().Single(e => e.Id.Value == "p1").Payload);
            Assert.Equal(preDeathX, recoveredX); // the match RESUMES at its pre-death state, not a reset

            // ---- A reconnecting client now resolves the room to node-B (the new owner), not the dead A. ----
            var routerB = new DirectoryRoomAffinityRouter(directoryB, NodeB);
            var decision = routerB.Resolve(room);
            Assert.Equal(RouteKind.Local, decision.Kind); // node-B serves it directly; the client reconnects here

            // The dead node, if it "returned", is fenced: it no longer owns the room (node-B does).
            Assert.True(directoryA.TryGetOwner(room, out var owner) && owner.Equals(NodeB));

            _output.WriteLine(
                $"node-A died mid-match (p1.x={preDeathX}); room '{room.RoomId.Value}' migrated to {NodeB.Value}, " +
                $"recovered to p1.x={recoveredX} from durable artifacts; reconnecting client routes Local to the survivor");
        }
    }
}
