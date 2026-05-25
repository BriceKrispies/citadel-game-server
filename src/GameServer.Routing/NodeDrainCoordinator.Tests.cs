using GameServer.Protocol;
using Xunit;

namespace GameServer.Routing;

/// <summary>
/// Drain correctness over the real <see cref="InMemoryRoomDirectory"/> + <see cref="CapacityAwareRoomPlacement"/>:
/// a draining node refuses new allocations and sheds every room it owns onto a live node, with no room
/// stranded (owner-less) or double-owned. Hermetic — the cross-node Redis proof is
/// <c>RedisNodeDrainScenario</c>.
/// </summary>
public sealed class NodeDrainCoordinatorTests
{
    private static readonly NodeId NodeA = new("node-A");
    private static readonly NodeId NodeB = new("node-B");

    private static RoomKey Room(string room) => new(new TenantId("tenant-a"), new RoomId(room));

    [Fact]
    public void Drain_ShedsEveryRoom_OntoALiveNode_NoneStrandedOrDoubleOwned()
    {
        var directory = new InMemoryRoomDirectory();
        // Cap 10 each; node-B has plenty of room to absorb node-A's rooms.
        var placement = new CapacityAwareRoomPlacement(directory, new[] { NodeA, NodeB }, maxRoomsPerNode: 10);

        // Place three rooms; with fleet order [A, B] and node-A having capacity, they land on node-A.
        var rooms = new[] { Room("r1"), Room("r2"), Room("r3") };
        foreach (var r in rooms)
        {
            Assert.Equal(NodeA, placement.Place(r).Owner);
        }

        Assert.Equal(3, directory.OwnedCount(NodeA));

        var outcomes = new NodeDrainCoordinator(directory, placement).Drain(NodeA);

        // Every room shed, every one re-homed onto node-B (the only other live node).
        Assert.Equal(3, outcomes.Count);
        Assert.All(outcomes, o => Assert.True(o.Result.IsPlaced));
        Assert.All(outcomes, o => Assert.Equal(NodeB, o.Result.Owner));

        // Node-A owns nothing now; node-B owns all three. No room is owner-less; none is owned twice.
        Assert.Equal(0, directory.OwnedCount(NodeA));
        Assert.Equal(3, directory.OwnedCount(NodeB));
        foreach (var r in rooms)
        {
            Assert.True(directory.TryGetOwner(r, out var owner));
            Assert.Equal(NodeB, owner);
        }
    }

    [Fact]
    public void DrainingNode_RefusesNewAllocations()
    {
        var directory = new InMemoryRoomDirectory();
        var placement = new CapacityAwareRoomPlacement(directory, new[] { NodeA, NodeB }, maxRoomsPerNode: 10);

        directory.SetNodeDraining(NodeA, true);

        // A fresh placement must skip the draining node-A and land on node-B.
        var placed = placement.Place(Room("new"));
        Assert.True(placed.IsPlaced);
        Assert.Equal(NodeB, placed.Owner);

        // The directory itself fences a direct claim on a draining node (defense in depth).
        Assert.False(directory.TryClaim(Room("direct"), NodeA));
    }

    [Fact]
    public void Drain_WhenClusterHasNoCapacity_LeavesRoomOwned_NotStranded()
    {
        // Single-node fleet: node-A drains but there is nowhere to shed to. The room must NOT be dropped
        // (stranded) — it stays owned by the draining node and the outcome reports cluster-at-capacity, so
        // an operator sees the drain could not complete rather than losing the room.
        var directory = new InMemoryRoomDirectory();
        var placement = new CapacityAwareRoomPlacement(directory, new[] { NodeA }, maxRoomsPerNode: 10);
        var room = Room("only");
        Assert.Equal(NodeA, placement.Place(room).Owner);

        var outcomes = new NodeDrainCoordinator(directory, placement).Drain(NodeA);

        Assert.Single(outcomes);
        Assert.Equal(RoomPlacementStatus.ClusterAtCapacity, outcomes[0].Result.Status);
        // The room is still owned by node-A — not lost.
        Assert.True(directory.TryGetOwner(room, out var owner));
        Assert.Equal(NodeA, owner);
    }

    [Fact]
    public void DrainedRoom_IsNotReHomedBackOntoTheDrainingNode()
    {
        // Even though node-A still has free capacity, a drained room must move to node-B, never back to A.
        var directory = new InMemoryRoomDirectory();
        var placement = new CapacityAwareRoomPlacement(directory, new[] { NodeA, NodeB }, maxRoomsPerNode: 100);
        var room = Room("r1");
        Assert.Equal(NodeA, placement.Place(room).Owner);

        new NodeDrainCoordinator(directory, placement).Drain(NodeA);

        Assert.True(directory.TryGetOwner(room, out var owner));
        Assert.Equal(NodeB, owner);
        Assert.NotEqual(NodeA, owner);
    }
}
