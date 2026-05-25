using GameServer.Protocol;
using Xunit;

namespace GameServer.Routing;

/// <summary>
/// Adversarial drain-under-contention proofs over the real <see cref="InMemoryRoomDirectory"/> +
/// <see cref="CapacityAwareRoomPlacement"/> (single-process authority). The drain re-place is
/// "release-after-secure" under one lock; these hammer it concurrently with other placement to prove the
/// two invariants that matter under contention: a room is never owner-less mid-shed and never co-owned by
/// two nodes, and the allocatable ceiling is never breached. The cross-node equivalents run on real Redis
/// (<c>RedisAllocationMultiNodeScenario</c>); these are the hermetic pins for the fast loop.
/// </summary>
public sealed class NodeDrainContentionTests
{
    private static readonly NodeId NodeA = new("node-A");
    private static readonly NodeId NodeB = new("node-B");

    private static RoomKey Room(string room) => new(new TenantId("tenant-a"), new RoomId(room));

    [Fact]
    public void Drain_ConcurrentWithFreshPlacement_NeverLeavesARoomOwnerlessOrDoubleOwned()
    {
        // Node-A owns a batch of rooms; while it drains, other threads place brand-new rooms. The directory
        // is a single shared instance, so every owner write/read goes through the same atomic map. After
        // the storm: every room (drained + fresh) has exactly one owner, no room is owner-less, and no node
        // exceeds its allocatable ceiling. Generous caps so contention — not capacity — is what is tested.
        var directory = new InMemoryRoomDirectory();
        var placement = new CapacityAwareRoomPlacement(directory, new[] { NodeA, NodeB }, maxRoomsPerNode: 1000);

        // Pin 40 rooms onto node-A by draining B while we seed, then re-activating B.
        directory.SetNodeDraining(NodeB, true);
        var owned = Enumerable.Range(0, 40).Select(i => Room($"owned-{i}")).ToArray();
        foreach (var r in owned)
        {
            Assert.Equal(NodeA, placement.Place(r).Owner);
        }

        directory.SetNodeDraining(NodeB, false);

        var fresh = Enumerable.Range(0, 40).Select(i => Room($"fresh-{i}")).ToArray();

        // Race: thread 0 drains node-A; the rest place fresh rooms at the same time.
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

        // Every room — drained and fresh — must have exactly one live owner; none owner-less, none on the
        // drained node-A, none double-owned (the directory can only hold one owner per key by construction).
        foreach (var r in owned.Concat(fresh))
        {
            Assert.True(directory.TryGetOwner(r, out var owner), $"room {r.RoomId.Value} is owner-less");
            Assert.NotEqual(NodeA, owner); // drained node must own nothing it sheds; fresh skips a draining node
            Assert.Equal(NodeB, owner);    // node-B is the only live node, so it must own all of them
        }

        // Drained node-A is left owning nothing; the ceiling was never breached on the live node.
        Assert.Equal(0, directory.OwnedCount(NodeA));
        Assert.Equal(owned.Length + fresh.Length, directory.OwnedCount(NodeB));
    }

    [Fact]
    public void TwoDrainsOfTheSameNode_Race_StillShedEveryRoomExactlyOnce_NoDoubleOwnership()
    {
        // Drain racing another drain of the SAME node (e.g. a retried admin call, or health-evict racing a
        // deploy). Both mark node-A draining and shed its rooms through the same placement. Re-placing an
        // already-shed room is idempotent (it returns the live owner), so the end state is exactly one
        // owner per room on node-B — never a room flipped back to A, never lost, never double-owned.
        var directory = new InMemoryRoomDirectory();
        var placement = new CapacityAwareRoomPlacement(directory, new[] { NodeA, NodeB }, maxRoomsPerNode: 1000);

        directory.SetNodeDraining(NodeB, true);
        var rooms = Enumerable.Range(0, 30).Select(i => Room($"r-{i}")).ToArray();
        foreach (var r in rooms)
        {
            Assert.Equal(NodeA, placement.Place(r).Owner);
        }

        directory.SetNodeDraining(NodeB, false);

        // Two concurrent drains of node-A.
        Parallel.Invoke(
            () => new NodeDrainCoordinator(directory, placement).Drain(NodeA),
            () => new NodeDrainCoordinator(directory, placement).Drain(NodeA));

        foreach (var r in rooms)
        {
            Assert.True(directory.TryGetOwner(r, out var owner));
            Assert.Equal(NodeB, owner);
        }

        Assert.Equal(0, directory.OwnedCount(NodeA));
        Assert.Equal(rooms.Length, directory.OwnedCount(NodeB));
    }
}
