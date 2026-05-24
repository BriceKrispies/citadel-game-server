using GameServer.Protocol;
using Xunit;

namespace GameServer.Routing;

/// <summary>
/// RED-phase pin for connection→room affinity routing. Ownership is set up with a working
/// <see cref="FakeRoomDirectory"/> so these target the routing seam specifically; they assert that a
/// locally-owned room is served here and a remotely-owned room redirects to its owner, and FAIL today
/// because <see cref="DirectoryRoomAffinityRouter"/> is unimplemented (connections are served by
/// whatever node they land on). They turn green once affinity routing consults the directory.
/// </summary>
public sealed class DirectoryRoomAffinityRouterTests
{
    private static RoomKey Room(string room) => new(new TenantId("tenant-a"), new RoomId(room));

    [Fact]
    public void Resolve_RoomOwnedByLocalNode_IsServedLocally()
    {
        var local = new NodeId("node-A");
        var directory = new FakeRoomDirectory();
        var room = Room("arena");
        directory.TryClaim(room, local); // the local node owns this room

        var router = new DirectoryRoomAffinityRouter(directory, local);

        var decision = router.Resolve(room);

        Assert.Equal(RouteKind.Local, decision.Kind);
    }

    [Fact]
    public void Resolve_RoomOwnedByAnotherNode_RedirectsToTheOwner()
    {
        var owner = new NodeId("node-A");
        var local = new NodeId("node-B");
        var directory = new FakeRoomDirectory();
        var room = Room("arena");
        directory.TryClaim(room, owner); // a different node owns this room

        var router = new DirectoryRoomAffinityRouter(directory, local);

        var decision = router.Resolve(room);

        Assert.Equal(RouteKind.Redirect, decision.Kind);
        Assert.Equal(owner, decision.Owner);
    }
}
