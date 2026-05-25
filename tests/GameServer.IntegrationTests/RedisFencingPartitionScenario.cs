using GameServer.Cluster.Redis;
using GameServer.Protocol;
using GameServer.Routing;
using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// Wave 4 adversarial fencing/partition proofs that only a REAL leased directory can give. Uses a SHORT
/// Redis lease so a "partition" can be simulated by simply letting the lease lapse (no live renewal),
/// after which another node legitimately claims the room — and the returning, fenced owner must NOT be
/// able to re-take or keep it. Also hammers drain against concurrent placement on one Redis to prove the
/// atomic Lua never strands or double-owns a room under contention. Skips (not passes) with no engine.
/// </summary>
/// <remarks>Integration scenario: requires a container engine (Podman/Docker); kept out of the fast unit loop.</remarks>
public sealed class RedisFencingPartitionScenario
{
    private static readonly NodeId NodeA = new("https://node-a:5000");
    private static readonly NodeId NodeB = new("https://node-b:5000");

    private readonly ITestOutputHelper _output;

    public RedisFencingPartitionScenario(ITestOutputHelper output) => _output = output;

    private static async Task<RedisContainer> StartRedisAsync()
    {
        try
        {
            var redis = new RedisBuilder("redis:7-alpine").Build();
            await redis.StartAsync();
            return redis;
        }
        catch (Exception ex)
        {
            throw new SkipException($"Docker/Redis not available in this environment: {ex.Message}");
        }
    }

    private static RoomKey Room(string room) => new(new TenantId("tenant-a"), new RoomId(room));

    /// <summary>
    /// Partition + fencing — the core split-brain invariant. Node-A owns a room on a SHORT lease, then
    /// "partitions": it stops renewing, so the lease lapses and the owner key expires. Node-B then claims
    /// the room (legitimately, via the acquisition path). When node-A "returns," its lease-renewal worker
    /// (modelled by <see cref="RedisRoomDirectory.TryRenew"/>, exactly what <c>RoomLeaseRenewalService</c>
    /// now calls) hammers the room. The renewal MUST NEVER re-acquire it — even if node-B's own lease has a
    /// gap — because renewal refreshes an existing lease, it does not acquire. A returning partitioned owner
    /// re-establishing ownership through renewal is the split brain the fence exists to prevent.
    /// <para>
    /// Regression guard: before TryRenew, the renewal worker called TryClaim, whose acquire-when-unowned
    /// branch let node-A steal the room back the instant node-B's lease lapsed — observed re-stealing it
    /// dozens of times in this exact setup. TryRenew has no acquire branch, so node-A is fenced.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task PartitionedOwner_RenewalNeverResurrectsOwnership_AfterAnotherNodeTookOver()
    {
        await using var redis = await StartRedisAsync();
        using var mux = await ConnectionMultiplexer.ConnectAsync(redis.GetConnectionString());
        // A short lease so we can model a partition by simply not renewing, and so node-B's lease will lapse
        // during node-A's barrage — the worst case: even with the room momentarily unowned, A's RENEWAL
        // must not grab it (only placement/affinity may acquire).
        var options = new RedisClusterOptions(KeyPrefix: $"partition-{Guid.NewGuid():n}", LeaseMs: 400);

        var fromNodeA = new RedisRoomDirectory(mux, options);
        var fromNodeB = new RedisRoomDirectory(mux, options);
        var room = Room("arena");

        // Node-A owns the room.
        Assert.True(fromNodeA.TryClaim(room, NodeA));
        Assert.True(fromNodeB.TryGetOwner(room, out var first) && first.Equals(NodeA));

        // Node-A partitions: it stops renewing. Wait past the lease so the owner key expires (Redis PX).
        await Task.Delay(options.LeaseMs + 250);

        // The room is now reclaimable: node-B legitimately takes it (acquisition path = TryClaim).
        Assert.True(fromNodeB.TryClaim(room, NodeB));
        Assert.True(fromNodeA.TryGetOwner(room, out var afterTakeover) && afterTakeover.Equals(NodeB));

        // Node-A "returns" and its renewal worker hammers TryRenew for the room still in its ActiveRooms.
        // Run long enough that node-B's short lease lapses mid-barrage (no live B renewer here) — the
        // strongest case. Not one renewal may resurrect node-A's ownership.
        var resurrected = 0;
        var deadline = DateTime.UtcNow.AddMilliseconds(options.LeaseMs * 3);
        Parallel.For(0, Environment.ProcessorCount * 4, _ =>
        {
            while (DateTime.UtcNow < deadline)
            {
                if (fromNodeA.TryRenew(room, NodeA))
                {
                    Interlocked.Increment(ref resurrected);
                }
            }
        });

        Assert.Equal(0, resurrected);
        // Node-A never re-owns the room through renewal: either node-B still owns it, or its lease lapsed and
        // it is simply unowned (awaiting a fresh placement) — but NEVER owned by node-A again.
        if (fromNodeA.TryGetOwner(room, out var owner))
        {
            Assert.Equal(NodeB, owner);
        }

        _output.WriteLine($"partition: node-A lease lapsed, node-B took '{room.RoomId.Value}'; returning node-A's renewal resurrected ownership {resurrected} times (must be 0)");
    }

    /// <summary>
    /// Drain-under-contention on real Redis: node-A drains while a flood of brand-new placements lands on the
    /// fleet concurrently. The whole place/shed decision is one atomic Lua script per call, so across the
    /// storm every room must end up owned by exactly one node, none owner-less, none on the drained node-A,
    /// and no node over its allocatable ceiling. This is the cross-node version of the in-memory
    /// NodeDrainContentionTests.
    /// </summary>
    [SkippableFact]
    public async Task Drain_ConcurrentWithFreshPlacement_OnRealRedis_NeverStrandsOrDoubleOwns()
    {
        await using var redis = await StartRedisAsync();
        using var mux = await ConnectionMultiplexer.ConnectAsync(redis.GetConnectionString());
        var options = new RedisClusterOptions(KeyPrefix: $"drain-race-{Guid.NewGuid():n}", LeaseMs: 30_000);

        var directory = new RedisRoomDirectory(mux, options);
        // Big caps so contention, not capacity, is what's exercised.
        var placement = new RedisRoomPlacement(mux, new[] { NodeA, NodeB }, maxRoomsPerNode: 1000, options);

        // Pin 30 rooms onto node-A (drain B while seeding, then re-activate B).
        directory.SetNodeDraining(NodeB, true);
        var owned = Enumerable.Range(0, 30).Select(i => Room($"owned-{i}")).ToArray();
        foreach (var r in owned)
        {
            Assert.Equal(NodeA, placement.Place(r).Owner);
        }

        directory.SetNodeDraining(NodeB, false);
        Assert.Equal(30, directory.OwnedCount(NodeA));

        var fresh = Enumerable.Range(0, 30).Select(i => Room($"fresh-{i}")).ToArray();

        // Race the drain against fresh placements.
        Parallel.For(0, fresh.Length + 1, i =>
        {
            if (i == 0)
            {
                new NodeDrainCoordinator(directory, placement).Drain(NodeA);
            }
            else
            {
                placement.Place(fresh[i - 1]);
            }
        });

        // Every room owned by exactly one live node (node-B); none stranded, none left on the drained A.
        foreach (var r in owned.Concat(fresh))
        {
            Assert.True(directory.TryGetOwner(r, out var owner), $"room {r.RoomId.Value} is owner-less");
            Assert.Equal(NodeB, owner);
        }

        Assert.Equal(0, directory.OwnedCount(NodeA));
        Assert.Equal(owned.Length + fresh.Length, directory.OwnedCount(NodeB));

        _output.WriteLine($"drain raced {fresh.Length} fresh placements on real Redis: all {owned.Length + fresh.Length} rooms single-owned on node-B, none stranded/double-owned");
    }

    /// <summary>
    /// Headroom under concurrency on real Redis: two "directors" race to place far more rooms than the
    /// allocatable ceiling allows. The ceiling is a GLOBAL invariant enforced inside the atomic Lua, so no
    /// node may exceed (cap - reservedHeadroom) regardless of interleaving — we assert the exact per-node
    /// counts and total, not merely "no exception".
    /// </summary>
    [SkippableFact]
    public async Task Headroom_UnderConcurrentPlacement_NeverExceedsTheAllocatableCeiling()
    {
        await using var redis = await StartRedisAsync();
        using var mux = await ConnectionMultiplexer.ConnectAsync(redis.GetConnectionString());
        var options = new RedisClusterOptions(KeyPrefix: $"headroom-conc-{Guid.NewGuid():n}", LeaseMs: 30_000);

        var directory = new RedisRoomDirectory(mux, options);
        // cap 5, reserve 2 -> allocatable 3 per node, 6 across the 2-node fleet. Two independent placement
        // instances = two directors racing over one Redis.
        var nodes = new[] { NodeA, NodeB };
        var director1 = new RedisRoomPlacement(mux, nodes, maxRoomsPerNode: 5, options, reservedHeadroom: 2);
        var director2 = new RedisRoomPlacement(mux, nodes, maxRoomsPerNode: 5, options, reservedHeadroom: 2);

        var rooms = Enumerable.Range(0, 40).Select(i => Room($"r{i}")).ToArray();
        var placed = 0;
        Parallel.ForEach(rooms.Select((r, idx) => (r, idx)), pair =>
        {
            var director = pair.idx % 2 == 0 ? director1 : director2;
            if (director.Place(pair.r).IsPlaced)
            {
                Interlocked.Increment(ref placed);
            }
        });

        // Exactly the allocatable ceiling across the fleet — never the hard cap (10), never more.
        Assert.Equal(6, placed);
        Assert.Equal(3, directory.OwnedCount(NodeA));
        Assert.Equal(3, directory.OwnedCount(NodeB));

        _output.WriteLine($"headroom under concurrency: {placed} placed across 2 racing directors (3/node), ceiling held, hard cap (10) never reached");
    }
}
