using GameServer.Cluster.Redis;
using GameServer.Protocol;
using GameServer.Routing;
using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// The real cross-node proof the in-process scenarios cannot give: two SEPARATE
/// <see cref="RedisRoomDirectory"/> instances (two "nodes") pointed at ONE Redis container must agree on
/// a single owner, fence each other, count ownership cross-instance, and place within capacity. Uses
/// Testcontainers to spin a throwaway Redis; if Docker is unavailable the test is SKIPPED (not passed)
/// with a clear reason, keeping the suite honest.
/// </summary>
/// <remarks>Integration scenario: requires Docker; kept out of the fast unit loop.</remarks>
public sealed class RedisRoomDirectoryScenario
{
    private readonly ITestOutputHelper _output;

    public RedisRoomDirectoryScenario(ITestOutputHelper output) => _output = output;

    [SkippableFact]
    public async Task TwoNodes_OverOneRedis_AgreeOnOwnership_FenceAndPlaceWithinCapacity()
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
            var options = new RedisClusterOptions(KeyPrefix: $"test-{Guid.NewGuid():n}", LeaseMs: 30_000);
            var nodeA = new NodeId("https://node-a:5000");
            var nodeB = new NodeId("https://node-b:5000");
            var room = new RoomKey(new TenantId("tenant-a"), new RoomId("arena"));

            // Two independent directory instances over the same Redis = two fleet nodes.
            var fromNodeA = new RedisRoomDirectory(mux, options);
            var fromNodeB = new RedisRoomDirectory(mux, options);

            Assert.True(fromNodeA.TryClaim(room, nodeA));            // node-A claims
            Assert.False(fromNodeB.TryClaim(room, nodeB));           // node-B is fenced — cross-instance
            Assert.True(fromNodeB.TryGetOwner(room, out var owner)); // node-B sees A as the owner
            Assert.Equal(nodeA, owner);
            Assert.Equal(1, fromNodeB.OwnedCount(nodeA));            // count is visible cross-instance

            fromNodeA.Release(room, nodeA);                          // owner releases
            Assert.True(fromNodeB.TryClaim(room, nodeB));            // now node-B may own it
            fromNodeB.Release(room, nodeB);

            // Cross-node placement over the same Redis honors capacity (separate key space).
            var placement = new RedisRoomPlacement(mux, new[] { nodeA, nodeB }, maxRoomsPerNode: 1, options with { KeyPrefix = options.KeyPrefix + "-place" });
            var p1 = placement.Place(new RoomKey(new TenantId("tenant-a"), new RoomId("r1")));
            var p2 = placement.Place(new RoomKey(new TenantId("tenant-a"), new RoomId("r2")));
            var p3 = placement.Place(new RoomKey(new TenantId("tenant-a"), new RoomId("r3")));

            Assert.True(p1.IsPlaced);
            Assert.True(p2.IsPlaced);
            Assert.NotEqual(p1.Owner, p2.Owner);                    // cap 1 → r2 spills to the other node
            Assert.Equal(RoomPlacementStatus.ClusterAtCapacity, p3.Status);
            Assert.Equal(p1.Owner, placement.Place(new RoomKey(new TenantId("tenant-a"), new RoomId("r1"))).Owner); // idempotent

            _output.WriteLine($"redis cluster verified: arena owner={owner.Value}; r1={p1.Owner.Value}, r2={p2.Owner.Value}, r3=full");
        }
    }
}
