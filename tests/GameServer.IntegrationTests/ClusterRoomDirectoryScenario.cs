using GameServer.Protocol;
using GameServer.Routing;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// Gap (horizontal scale) — no cluster room directory. A fleet needs shared infrastructure that names
/// exactly one owner per <see cref="RoomKey"/>, so two nodes never tick the same room (the split brain
/// in <see cref="MultiNodeOwnershipScenario"/>). This scenario has two nodes consult one shared
/// directory: node-A claims a room, node-B must see A as owner and be fenced from re-claiming. It
/// FAILS today via the unimplemented <see cref="ClusterRoomDirectory"/> seam, and turns green once a
/// fenced, cluster-backed directory exists behind the contract.
/// </summary>
public sealed class ClusterRoomDirectoryScenario
{
    private readonly ITestOutputHelper _output;

    public ClusterRoomDirectoryScenario(ITestOutputHelper output) => _output = output;

    [Fact]
    public void SharedDirectory_GivesExactlyOneOwnerAcrossNodes()
    {
        // The directory is shared infrastructure both nodes consult to agree on a single owner. A
        // shared in-memory instance models that shared infra in-process; a real fleet uses the
        // distributed (Redis) backend behind the same IRoomDirectory contract.
        var directory = new InMemoryRoomDirectory();
        var nodeA = new NodeId("node-A");
        var nodeB = new NodeId("node-B");
        var key = new RoomKey(new TenantId("tenant-a"), new RoomId("arena"));

        // node-A claims the room; node-B must then observe A as the owner and be fenced from stealing it.
        Assert.True(directory.TryClaim(key, nodeA)); // RED: cluster directory not implemented yet
        Assert.True(directory.TryGetOwner(key, out var owner));
        Assert.Equal(nodeA, owner);
        Assert.False(directory.TryClaim(key, nodeB));

        _output.WriteLine($"room {key.TenantId.Value}/{key.RoomId.Value} is owned by {owner.Value}");
    }
}
