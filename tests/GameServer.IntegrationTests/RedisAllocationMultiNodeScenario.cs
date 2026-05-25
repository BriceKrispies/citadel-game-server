using GameServer.Cluster.Redis;
using GameServer.Protocol;
using GameServer.Routing;
using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// Wave 4 cross-node allocation/capacity/drain proofs that only a REAL distributed directory can give —
/// two independent <see cref="RedisRoomDirectory"/> / <see cref="RedisRoomPlacement"/> instances ("nodes")
/// over ONE Redis container, exercising the atomic claim/place/drain Lua. Testcontainers spins a throwaway
/// Redis; with no engine each fact SKIPS (not passes) with a clear reason, keeping the fast loop hermetic
/// (the headroom/drain logic also has hermetic unit pins in <c>GameServer.Routing</c>).
/// </summary>
/// <remarks>Integration scenario: requires a container engine (Podman/Docker); kept out of the fast unit loop.</remarks>
public sealed class RedisAllocationMultiNodeScenario
{
    private static readonly NodeId NodeA = new("https://node-a:5000");
    private static readonly NodeId NodeB = new("https://node-b:5000");

    private readonly ITestOutputHelper _output;

    public RedisAllocationMultiNodeScenario(ITestOutputHelper output) => _output = output;

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
    /// Test #5. Two nodes over one Redis must agree on a SINGLE owner: once node-A claims a room, node-B's
    /// claim is fenced (returns false) and node-B sees node-A as the owner. This is the split-brain fix —
    /// the same atomic SET-NX-PX fence the host's affinity/placement path relies on, proven cross-instance.
    /// </summary>
    [SkippableFact]
    public async Task SingleOwnerAcrossNodes_SecondNodeRefusesAnAlreadyOwnedRoom()
    {
        await using var redis = await StartRedisAsync();
        using var mux = await ConnectionMultiplexer.ConnectAsync(redis.GetConnectionString());
        var options = new RedisClusterOptions(KeyPrefix: $"single-{Guid.NewGuid():n}", LeaseMs: 30_000);

        // Two independent directory instances over the same Redis = two fleet nodes.
        var fromNodeA = new RedisRoomDirectory(mux, options);
        var fromNodeB = new RedisRoomDirectory(mux, options);
        var room = Room("arena");

        Assert.True(fromNodeA.TryClaim(room, NodeA));               // node-A becomes the sole owner
        Assert.False(fromNodeB.TryClaim(room, NodeB));              // node-B is FENCED — no split brain
        Assert.True(fromNodeB.TryGetOwner(room, out var owner));    // node-B sees A as the single owner
        Assert.Equal(NodeA, owner);

        // Even a barrage of concurrent claims from node-B never steals the room.
        var stolen = 0;
        Parallel.For(0, 32, _ =>
        {
            if (fromNodeB.TryClaim(room, NodeB))
            {
                Interlocked.Increment(ref stolen);
            }
        });
        Assert.Equal(0, stolen);
        Assert.True(fromNodeA.TryGetOwner(room, out var still) && still.Equals(NodeA));

        _output.WriteLine($"single owner across nodes: '{room.TenantId.Value}/{room.RoomId.Value}' owned by {owner.Value}; node-B fenced (0/33 claims won)");
    }

    /// <summary>
    /// NodeDrain. A draining node (1) refuses NEW allocations and (2) sheds the rooms it already owns onto a
    /// live node — no room stranded (owner-less) or double-owned. Driven through the real cross-node
    /// <see cref="RedisRoomPlacement"/> + <see cref="NodeDrainCoordinator"/> over one Redis.
    /// </summary>
    [SkippableFact]
    public async Task NodeDrain_RefusesNewAllocations_AndShedsExistingRoomsToALiveNode()
    {
        await using var redis = await StartRedisAsync();
        using var mux = await ConnectionMultiplexer.ConnectAsync(redis.GetConnectionString());
        var options = new RedisClusterOptions(KeyPrefix: $"drain-{Guid.NewGuid():n}", LeaseMs: 30_000);

        var directory = new RedisRoomDirectory(mux, options);
        // Cap 10 each so node-B can absorb everything node-A sheds.
        var placement = new RedisRoomPlacement(mux, new[] { NodeA, NodeB }, maxRoomsPerNode: 10, options);

        // Force three rooms onto node-A by draining B first, placing, then un-draining B.
        directory.SetNodeDraining(NodeB, true);
        var rooms = new[] { Room("r1"), Room("r2"), Room("r3") };
        foreach (var r in rooms)
        {
            Assert.Equal(NodeA, placement.Place(r).Owner);
        }

        directory.SetNodeDraining(NodeB, false);
        Assert.Equal(3, directory.OwnedCount(NodeA));

        // Drain node-A: it must refuse new rooms and shed its three onto node-B.
        var outcomes = new NodeDrainCoordinator(directory, placement).Drain(NodeA);

        Assert.Equal(3, outcomes.Count);
        Assert.All(outcomes, o => Assert.True(o.Result.IsPlaced));
        Assert.All(outcomes, o => Assert.Equal(NodeB, o.Result.Owner));

        // Ownership moved cleanly: node-A owns nothing, node-B owns all three, each owned by exactly one node.
        Assert.Equal(0, directory.OwnedCount(NodeA));
        Assert.Equal(3, directory.OwnedCount(NodeB));
        foreach (var r in rooms)
        {
            Assert.True(directory.TryGetOwner(r, out var owner));
            Assert.Equal(NodeB, owner);
        }

        // A draining node refuses a brand-new allocation too (it lands on node-B).
        Assert.True(directory.IsNodeDraining(NodeA));
        Assert.False(directory.TryClaim(Room("fresh-direct"), NodeA)); // directory-level fence

        _output.WriteLine($"drained {NodeA.Value}: shed 3 rooms -> {NodeB.Value}, none stranded/double-owned; node-A fenced from new claims");
    }

    /// <summary>
    /// CapacityHeadroom. Reserved headroom keeps a node from being filled to its hard ceiling: with cap 5
    /// and 2 reserved, each node takes only 3 rooms across the fleet — proven cross-node through the Redis
    /// placement Lua.
    /// </summary>
    [SkippableFact]
    public async Task CapacityHeadroom_PlacementStopsAtAllocatableCeiling_NotTheHardCap()
    {
        await using var redis = await StartRedisAsync();
        using var mux = await ConnectionMultiplexer.ConnectAsync(redis.GetConnectionString());
        var options = new RedisClusterOptions(KeyPrefix: $"headroom-{Guid.NewGuid():n}", LeaseMs: 30_000);

        var directory = new RedisRoomDirectory(mux, options);
        // Hard cap 5, reserve 2 -> allocatable 3 per node, 6 across the 2-node fleet.
        var placement = new RedisRoomPlacement(mux, new[] { NodeA, NodeB }, maxRoomsPerNode: 5, options, reservedHeadroom: 2);

        var placed = 0;
        for (var i = 0; i < 20; i++)
        {
            if (placement.Place(Room($"r{i}")).IsPlaced)
            {
                placed++;
            }
        }

        Assert.Equal(6, placed);                          // 2 nodes x 3 allocatable, NOT 2 x 5 = 10
        Assert.Equal(3, directory.OwnedCount(NodeA));
        Assert.Equal(3, directory.OwnedCount(NodeB));

        _output.WriteLine($"headroom respected: cap 5 reserve 2 -> {placed} placed (3 per node), reserved slots left free");
    }
}
