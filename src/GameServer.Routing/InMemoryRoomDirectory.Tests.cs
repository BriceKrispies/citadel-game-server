using GameServer.Protocol;
using Xunit;

namespace GameServer.Routing;

/// <summary>
/// Behavioral pin for the in-memory room directory: one owner per room with fencing, owner-gated
/// release, and per-owner counts. Was RED while the directory was an unimplemented seam; green now that
/// <see cref="InMemoryRoomDirectory"/> is implemented.
/// </summary>
public sealed class InMemoryRoomDirectoryTests
{
    private static RoomKey Room(string room) => new(new TenantId("tenant-a"), new RoomId(room));

    [Fact]
    public void Claim_GivesExactlyOneOwner_AndFencesCompetingNodes()
    {
        var directory = new InMemoryRoomDirectory();
        var nodeA = new NodeId("node-A");
        var nodeB = new NodeId("node-B");
        var room = Room("arena");

        Assert.True(directory.TryClaim(room, nodeA));              // node-A becomes the sole owner
        Assert.True(directory.TryClaim(room, nodeA));              // idempotent for the holder
        Assert.True(directory.TryGetOwner(room, out var owner));
        Assert.Equal(nodeA, owner);
        Assert.False(directory.TryClaim(room, nodeB));             // a second node cannot steal an owned room
    }

    [Fact]
    public void Release_ByOwner_FreesTheRoomForAnotherNodeToClaim()
    {
        var directory = new InMemoryRoomDirectory();
        var nodeA = new NodeId("node-A");
        var nodeB = new NodeId("node-B");
        var room = Room("arena");

        directory.TryClaim(room, nodeA);
        directory.Release(room, nodeB);                            // not the owner → no-op
        Assert.False(directory.TryClaim(room, nodeB));             // still owned by A
        directory.Release(room, nodeA);                            // owner releases
        Assert.True(directory.TryClaim(room, nodeB));              // freed → another node may now own it
    }

    [Fact]
    public void OwnedCount_TracksRoomsPerOwner()
    {
        var directory = new InMemoryRoomDirectory();
        var nodeA = new NodeId("node-A");

        Assert.Equal(0, directory.OwnedCount(nodeA));
        directory.TryClaim(Room("r1"), nodeA);
        directory.TryClaim(Room("r2"), nodeA);
        Assert.Equal(2, directory.OwnedCount(nodeA));
        directory.Release(Room("r1"), nodeA);
        Assert.Equal(1, directory.OwnedCount(nodeA));
    }

    [Fact]
    public void Claim_UnderConcurrency_HasExactlyOneWinner()
    {
        // Many nodes race to claim the SAME room at once. The fence must admit exactly one — the
        // outcome (a single winner) is deterministic even though which node wins is not.
        var directory = new InMemoryRoomDirectory();
        var room = Room("arena");
        const int contenders = 64;
        var nodes = Enumerable.Range(0, contenders).Select(i => new NodeId($"node-{i}")).ToArray();

        var wins = 0;
        Parallel.For(0, contenders, i =>
        {
            if (directory.TryClaim(room, nodes[i]))
            {
                Interlocked.Increment(ref wins);
            }
        });

        Assert.Equal(1, wins);
        Assert.True(directory.TryGetOwner(room, out var owner));
        Assert.Contains(owner, nodes);
        Assert.Equal(1, directory.OwnedCount(owner));
    }
}
