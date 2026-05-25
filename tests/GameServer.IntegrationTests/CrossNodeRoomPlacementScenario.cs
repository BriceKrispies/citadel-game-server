using GameServer.Protocol;
using GameServer.Routing;
using GameServer.Simulation;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// Gap (horizontal scale) — no cross-node room placement. The RED counterpart to the demonstrate-only
/// <see cref="MultiNodeOwnershipScenario"/>: that test asserts the split brain happens; this one
/// asserts the fix — a cluster placement assigns each <see cref="RoomKey"/> to exactly one owning
/// node, idempotently, so both nodes resolve the same owner and the room cannot diverge. It first
/// shows the current divergence as evidence, then FAILS via the unimplemented
/// <see cref="CapacityAwareRoomPlacement"/> seam, turning green once placement yields one owner
/// cluster-wide.
/// </summary>
public sealed class CrossNodeRoomPlacementScenario
{
    private readonly ITestOutputHelper _output;

    public CrossNodeRoomPlacementScenario(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task SameRoom_GetsOneOwnerAcrossNodes_SoStateCannotDiverge()
    {
        const string tenant = "tenant-a", room = "arena", player = "p1";
        var key = new RoomKey(new TenantId(tenant), new RoomId(room));

        // Current reality (evidence): two independent nodes each place the room locally and diverge.
        var nodeA = new IntegrationHarness(_ => new MoveRightGame());
        var nodeB = new IntegrationHarness(_ => new MoveRightGame());
        await nodeA.RunClientAsync(tenant, room, player, "demo", Enumerable.Repeat(MoveRightGame.MoveRight, 5).ToArray());
        await nodeB.RunClientAsync(tenant, room, player, "demo", Enumerable.Repeat(MoveRightGame.MoveRight, 2).ToArray());
        await nodeA.Server.TickRoom(key);
        await nodeB.Server.TickRoom(key);
        var xOnA = ReadX(nodeA, key, player);
        var xOnB = ReadX(nodeB, key, player);
        _output.WriteLine($"without placement: node-A sees x={xOnA}, node-B sees x={xOnB} for the SAME room");

        // Target behavior: a cluster placement assigns the room to ONE node drawn from the cluster,
        // idempotently, and records that owner in the shared directory — so every node resolves the
        // same owner and only that node is ever authoritative.
        var directory = new TrackingDirectory();
        var nodes = new[] { new NodeId("node-A"), new NodeId("node-B") };
        var placement = new CapacityAwareRoomPlacement(directory, nodes, maxRoomsPerNode: 1000);

        var placed = placement.Place(key); // RED: cross-node placement not implemented yet

        Assert.True(placed.IsPlaced);
        Assert.Contains(placed.Owner, nodes);                     // owner is a real node in the cluster
        Assert.Equal(placed, placement.Place(key));               // idempotent: same room → same placement
        Assert.True(directory.TryGetOwner(key, out var recorded));
        Assert.Equal(placed.Owner, recorded);                     // placement wrote the owner to the directory
    }

    /// <summary>A minimal working directory so placement can record ownership and the test read it back.</summary>
    private sealed class TrackingDirectory : IRoomDirectory
    {
        private readonly Dictionary<RoomKey, NodeId> _owners = new();

        public bool TryClaim(RoomKey room, NodeId owner)
        {
            if (_owners.TryGetValue(room, out var existing))
            {
                return existing == owner;
            }

            _owners[room] = owner;
            return true;
        }

        public bool TryGetOwner(RoomKey room, out NodeId owner) => _owners.TryGetValue(room, out owner);

        public int OwnedCount(NodeId owner) => _owners.Values.Count(o => o.Equals(owner));

        public void Release(RoomKey room, NodeId owner)
        {
            if (_owners.TryGetValue(room, out var existing) && existing == owner)
            {
                _owners.Remove(room);
            }
        }

        public void SetNodeDraining(NodeId node, bool draining)
        {
        }

        public bool IsNodeDraining(NodeId node) => false;

        public IReadOnlyCollection<RoomKey> OwnedRooms(NodeId owner) =>
            _owners.Where(e => e.Value.Equals(owner)).Select(e => e.Key).ToArray();
    }

    private static int ReadX(IntegrationHarness node, RoomKey key, string player)
    {
        Assert.True(node.Router.TryGetRoom(key, out var room));
        return MoveRightGame.DecodeX(room.Project().Single(e => e.Id.Value == player).Payload);
    }
}
