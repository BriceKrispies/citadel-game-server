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
    /// Partition + fencing — the core split-brain invariant. Node-A owns a room, then "partitions": its
    /// lease lapses and the owner key expires. Node-B then claims the room (legitimately, via the
    /// acquisition path). When node-A "returns," its lease-renewal worker (modelled by
    /// <see cref="RedisRoomDirectory.TryRenew"/>, exactly what <c>RoomLeaseRenewalService</c> now calls)
    /// hammers the room. The renewal MUST NEVER re-acquire it — neither while node-B owns it NOR while the
    /// room is unowned — because renewal refreshes an existing lease, it does not acquire. A returning
    /// partitioned owner re-establishing ownership through renewal is the split brain the fence prevents.
    /// <para>
    /// Regression guard: before TryRenew, the renewal worker called TryClaim, whose acquire-when-unowned
    /// branch let node-A steal the room back the instant node-B's lease lapsed — observed re-stealing it
    /// dozens of times. TryRenew has no acquire branch, so node-A is fenced.
    /// </para>
    /// <para>
    /// Deterministic by construction: the "partition" is modelled by DELETING the owner key (exactly what a
    /// Redis <c>PX</c> lease expiry does) rather than sleeping past a real lease, and the lease is generous
    /// so an ownership read can never race a scheduling stall. No wall-clock, no <c>Task.Delay</c> — the
    /// proof holds regardless of CPU load (a short-lease/real-time version was flaky under full-suite load).
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task PartitionedOwner_RenewalNeverResurrectsOwnership_AfterAnotherNodeTookOver()
    {
        await using var redis = await StartRedisAsync();
        using var mux = await ConnectionMultiplexer.ConnectAsync(redis.GetConnectionString());
        // Generous lease: ownership reads in this test must never race a scheduling stall. The partition is
        // simulated deterministically by expiring (deleting) the owner key, not by waiting out a short lease.
        var options = new RedisClusterOptions(KeyPrefix: $"partition-{Guid.NewGuid():n}", LeaseMs: 30_000);
        var db = mux.GetDatabase();

        var fromNodeA = new RedisRoomDirectory(mux, options);
        var fromNodeB = new RedisRoomDirectory(mux, options);
        var room = Room("arena");
        var ownerKey = RedisClusterKeys.OwnerKey(options.KeyPrefix, room);

        // Node-A owns the room.
        Assert.True(fromNodeA.TryClaim(room, NodeA));
        Assert.True(fromNodeB.TryGetOwner(room, out var first) && first.Equals(NodeA));

        // Node-A partitions: its lease lapses. Model that deterministically by expiring the owner key —
        // exactly what Redis PX expiry does — so there is no real-time race.
        Assert.True(db.KeyDelete(ownerKey));

        // The room is now reclaimable: node-B legitimately takes it (acquisition path = TryClaim).
        Assert.True(fromNodeB.TryClaim(room, NodeB));
        Assert.True(fromNodeA.TryGetOwner(room, out var afterTakeover) && afterTakeover.Equals(NodeB));

        // Node-A "returns" and its renewal worker hammers TryRenew for the room still in its ActiveRooms.
        // While node-B owns it, not one renewal may resurrect node-A's ownership.
        var resurrected = HammerRenew(fromNodeA, room);
        Assert.Equal(0, resurrected);
        Assert.True(fromNodeA.TryGetOwner(room, out var stillB) && stillB.Equals(NodeB));

        // Strongest case — the room is now UNOWNED (node-B's lease also lapses with no renewer). Model it
        // deterministically by expiring node-B's owner key. Even owner-less, node-A's renewal must STILL
        // refuse to acquire: the only legitimate (re)acquisition path is placement/affinity, never renewal.
        Assert.True(db.KeyDelete(ownerKey));
        Assert.False(fromNodeA.TryGetOwner(room, out _)); // genuinely unowned

        var resurrectedWhileUnowned = HammerRenew(fromNodeA, room);
        Assert.Equal(0, resurrectedWhileUnowned);

        // Node-A never re-owns the room through renewal — it remains unowned, awaiting a fresh placement.
        Assert.False(fromNodeA.TryGetOwner(room, out _));

        _output.WriteLine($"partition (deterministic): node-A expired, node-B took '{room.RoomId.Value}', then unowned; node-A renewal resurrected ownership {resurrected}+{resurrectedWhileUnowned} times (must be 0)");
    }

    /// <summary>Hammers <see cref="RedisRoomDirectory.TryRenew"/> for node-A across many threads (bounded
    /// iterations, no wall-clock) and returns how many calls resurrected ownership — must always be 0.</summary>
    private static int HammerRenew(RedisRoomDirectory fromNodeA, RoomKey room)
    {
        var resurrected = 0;
        Parallel.For(0, Environment.ProcessorCount * 4, _ =>
        {
            for (var i = 0; i < 50; i++)
            {
                if (fromNodeA.TryRenew(room, NodeA))
                {
                    Interlocked.Increment(ref resurrected);
                }
            }
        });
        return resurrected;
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
