using GameServer.Protocol;
using GameServer.Routing;
using GameServer.Simulation;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// Gap (horizontal scale) — no connection→room affinity routing. With a fleet behind a room-agnostic
/// load balancer, a connection for a room can land on any node, and that node serves it locally —
/// creating a second authoritative copy of a room another node owns. This scenario shows that current
/// behavior (node-B serves a room it does not own) and then asserts the target: a connection for a
/// remotely-owned room must redirect to the owner. It FAILS today via the unimplemented
/// <see cref="DirectoryRoomAffinityRouter"/> seam, and turns green once affinity routing consults the
/// directory and redirects non-local rooms.
/// </summary>
public sealed class ConnectionAffinityRoutingScenario
{
    private readonly ITestOutputHelper _output;

    public ConnectionAffinityRoutingScenario(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task ConnectionForRemotelyOwnedRoom_MustRedirectToOwner_NotServeLocally()
    {
        const string tenant = "tenant-a", room = "arena", player = "p1";
        var key = new RoomKey(new TenantId(tenant), new RoomId(room));
        var nodeA = new NodeId("node-A"); // owns the room
        var nodeB = new NodeId("node-B"); // where a connection happens to land

        // Current reality (evidence): with no affinity routing, node-B serves the room locally,
        // standing up a second authoritative copy — exactly what affinity routing must prevent.
        var bStack = new IntegrationHarness(_ => new MoveRightGame());
        await bStack.RunClientAsync(tenant, room, player, "demo", new[] { MoveRightGame.MoveRight });
        await bStack.Server.TickRoom(key);
        Assert.True(bStack.Router.TryGetRoom(key, out _));
        _output.WriteLine("node-B served a room it does not own — no affinity routing today");

        // Target behavior: node-A owns the room (per the directory); a connection for it arriving at
        // node-B must resolve to a redirect to node-A, not be served locally.
        IRoomDirectory directory = new OwnedDirectory(key, nodeA);
        var affinity = new DirectoryRoomAffinityRouter(directory, nodeB);

        var decision = affinity.Resolve(key); // RED: affinity routing not implemented yet

        Assert.Equal(RouteKind.Redirect, decision.Kind);
        Assert.Equal(nodeA, decision.Owner);
    }

    /// <summary>A minimal working directory with one pre-owned room, to set up the affinity check.</summary>
    private sealed class OwnedDirectory : IRoomDirectory
    {
        private readonly RoomKey _room;
        private readonly NodeId _owner;

        public OwnedDirectory(RoomKey room, NodeId owner)
        {
            _room = room;
            _owner = owner;
        }

        public bool TryClaim(RoomKey room, NodeId owner) => true;

        public bool TryGetOwner(RoomKey room, out NodeId owner)
        {
            owner = _owner;
            return room == _room;
        }

        public int OwnedCount(NodeId owner) => owner.Equals(_owner) ? 1 : 0;

        public void Release(RoomKey room, NodeId owner)
        {
        }
    }
}
