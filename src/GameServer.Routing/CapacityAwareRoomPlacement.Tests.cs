using GameServer.Protocol;
using Xunit;

namespace GameServer.Routing;

/// <summary>
/// RED-phase pin for cross-node room placement. These assert the target behavior — placement keyed
/// idempotently by <see cref="RoomKey"/>, genuine capacity awareness (spill when a node is full, then
/// <see cref="RoomPlacementStatus.ClusterAtCapacity"/> when all are), and recording the owner in the
/// directory — and FAIL today because <see cref="CapacityAwareRoomPlacement"/> is unimplemented. They
/// are written so a capacity-blind round-robin or a constant-node implementation cannot satisfy them;
/// only a faithful implementation turns them green.
/// </summary>
public sealed class CapacityAwareRoomPlacementTests
{
    private static readonly NodeId NodeA = new("node-A");
    private static readonly NodeId NodeB = new("node-B");

    private static RoomKey Room(string room) => new(new TenantId("tenant-a"), new RoomId(room));

    private static CapacityAwareRoomPlacement Placement(IRoomDirectory directory, int maxRoomsPerNode) =>
        new(directory, new[] { NodeA, NodeB }, maxRoomsPerNode);

    [Fact]
    public void CapacityHeadroom_ReservedSlots_AreNotFilledByPlacement()
    {
        // Hard ceiling 5, but reserve 2 → each node is only filled to 3. The reserved slots stay free so a
        // node can absorb shed/migrated rooms and ride out a burst without being driven to its ceiling.
        var directory = new InMemoryRoomDirectory();
        var placement = new CapacityAwareRoomPlacement(directory, new[] { NodeA, NodeB }, maxRoomsPerNode: 5, reservedHeadroom: 2);

        var placed = 0;
        for (var i = 0; i < 20; i++)
        {
            if (placement.Place(Room($"r{i}")).IsPlaced)
            {
                placed++;
            }
        }

        // 2 nodes × (5 - 2 allocatable) = 6 placed; the rest report cluster-at-capacity even though the
        // raw cap would allow 10. No node is filled past its allocatable ceiling.
        Assert.Equal(6, placed);
        Assert.Equal(3, directory.OwnedCount(NodeA));
        Assert.Equal(3, directory.OwnedCount(NodeB));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]   // == cap leaves zero allocatable
    [InlineData(6)]   // > cap
    public void ReservedHeadroom_OutOfRange_IsRejected(int reservedHeadroom)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CapacityAwareRoomPlacement(new FakeRoomDirectory(), new[] { NodeA }, maxRoomsPerNode: 5, reservedHeadroom: reservedHeadroom));
    }

    [Fact]
    public void Place_IsIdempotentPerRoomKey()
    {
        var placement = Placement(new FakeRoomDirectory(), maxRoomsPerNode: 100);

        var r1 = placement.Place(Room("r1"));
        var r2 = placement.Place(Room("r2")); // a different room placed in between

        Assert.True(r1.IsPlaced);
        Assert.True(r2.IsPlaced);
        // Re-placing returns each ROOM's own owner — proving placement is keyed by RoomKey, not
        // "the last placed" and not a global constant blind to which room is asked.
        Assert.Equal(r1.Owner, placement.Place(Room("r1")).Owner);
        Assert.Equal(r2.Owner, placement.Place(Room("r2")).Owner);
    }

    [Fact]
    public void Place_RespectsCapacity_SpillsThenReportsClusterFull()
    {
        var placement = Placement(new FakeRoomDirectory(), maxRoomsPerNode: 1); // 2 nodes × 1 = 2 rooms max

        var first = placement.Place(Room("r1"));
        var second = placement.Place(Room("r2"));
        var third = placement.Place(Room("r3"));

        Assert.True(first.IsPlaced);
        Assert.True(second.IsPlaced);
        // The first node is full after r1, so r2 MUST spill to the other node — a capacity-blind
        // placement that packed r2 onto the same node would fail here.
        Assert.NotEqual(first.Owner, second.Owner);
        // Both nodes are now full, so r3 cannot be placed — a round-robin ignoring capacity would
        // wrongly return Placed here.
        Assert.Equal(RoomPlacementStatus.ClusterAtCapacity, third.Status);
    }

    [Fact]
    public void Place_RecordsTheOwnerInTheDirectory()
    {
        var directory = new FakeRoomDirectory();
        var placement = Placement(directory, maxRoomsPerNode: 100);
        var room = Room("r1");

        var result = placement.Place(room);

        Assert.True(result.IsPlaced);
        Assert.True(directory.TryGetOwner(room, out var owner)); // placement must write the assignment
        Assert.Equal(result.Owner, owner);
    }

    [Fact]
    public void Place_ReclaimsRoomOwnedByANodeOutsideTheFleet()
    {
        // A node that owned this room was scaled down / removed from the fleet. Placement must not keep
        // returning the dead owner (which would redirect clients to a dead address) — it re-places on a
        // live node.
        var directory = new FakeRoomDirectory();
        var dead = new NodeId("node-removed");
        var room = Room("r1");
        directory.TryClaim(room, dead);

        var result = Placement(directory, maxRoomsPerNode: 100).Place(room);

        Assert.True(result.IsPlaced);
        Assert.NotEqual(dead, result.Owner);
        Assert.Contains(result.Owner, new[] { NodeA, NodeB });
    }

    [Fact]
    public void Place_UnderConcurrency_NeverExceedsClusterCapacity()
    {
        // 2 nodes × cap 2 = 4 slots; 16 distinct rooms placed concurrently. Capacity is a global
        // invariant: exactly 4 must be placed and the remaining 12 must report cluster-full — never
        // more than the cap on any node, regardless of interleaving. Uses the real thread-safe
        // InMemoryRoomDirectory (not the fake) so the directory's own concurrency is exercised, not
        // masked by the placement lock.
        var directory = new InMemoryRoomDirectory();
        var placement = Placement(directory, maxRoomsPerNode: 2);
        const int rooms = 16;

        var placed = 0;
        var full = 0;
        Parallel.For(0, rooms, i =>
        {
            var result = placement.Place(Room($"r{i}"));
            if (result.IsPlaced)
            {
                Interlocked.Increment(ref placed);
            }
            else
            {
                Interlocked.Increment(ref full);
            }
        });

        Assert.Equal(4, placed);
        Assert.Equal(rooms - 4, full);
        Assert.Equal(2, directory.OwnedCount(NodeA));
        Assert.Equal(2, directory.OwnedCount(NodeB));
    }
}
