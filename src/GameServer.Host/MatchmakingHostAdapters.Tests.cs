using GameServer.ControlPlane;
using GameServer.Identity;
using GameServer.Matchmaking;
using GameServer.Protocol;
using GameServer.Routing;

namespace GameServer.Host;

/// <summary>
/// Unit tests for the composition-root adapters that bridge matchmaking's ports to the real Routing
/// allocation and Identity token mint. Compiled into the integration-test assembly (which references
/// the Host); the analyzer detects this co-located file so the adapters carry a test.
/// </summary>
public sealed class MatchmakingHostAdaptersTests
{
    private static MatchScope Scope() => new(new TenantId("tenant-a"), new GameId("demo-game"), 1);

    [Fact]
    public void RoutingRoomAllocator_CreatesAndPlacesRoom_ReturningItsId()
    {
        var rooms = new InMemoryRoomRegistry();
        var directory = new InMemoryRoomDirectory();
        var placement = new CapacityAwareRoomPlacement(directory, new[] { new NodeId("node-1") }, maxRoomsPerNode: 10);
        var allocator = new RoutingRoomAllocator(rooms, placement);

        var room = allocator.Allocate(Scope());

        Assert.NotNull(room);
        Assert.True(rooms.TryGet(room!.Value.Value, out var stored));
        Assert.Equal("tenant-a", stored.TenantId);
        // The room was placed on the only node, so the directory now owns it.
        Assert.True(directory.TryGetOwner(new RoomKey(new TenantId("tenant-a"), room.Value), out _));
    }

    [Fact]
    public void RoutingRoomAllocator_ReturnsNull_WhenClusterIsFull()
    {
        var rooms = new InMemoryRoomRegistry();
        var directory = new InMemoryRoomDirectory();
        // One slot, already filled, so the next allocation cannot be placed.
        var placement = new CapacityAwareRoomPlacement(directory, new[] { new NodeId("node-1") }, maxRoomsPerNode: 1);
        var allocator = new RoutingRoomAllocator(rooms, placement);

        Assert.NotNull(allocator.Allocate(Scope()));
        Assert.Null(allocator.Allocate(Scope()));
    }

    [Fact]
    public void JoinTokenServiceIssuer_MintsAVerifiableTokenScopedToTheMatch()
    {
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var codec = new Hs256JoinTokenCodec("test-secret-test-secret-test-secret", clock, TimeSpan.FromMinutes(5));
        var issuer = new JoinTokenServiceIssuer(new JoinTokenService(codec));

        var token = issuer.IssueJoinToken(Scope(), new RoomId("room-9"), new PlayerId("alice"));

        var verified = codec.Verify(token);
        Assert.True(verified.IsOk);
        Assert.Equal("tenant-a", verified.Claims!.TenantId);
        Assert.Equal("demo-game", verified.Claims.GameId);
        Assert.Equal("room-9", verified.Claims.RoomId);
        Assert.Equal("alice", verified.Claims.PlayerId);
    }

    private sealed class FixedClock : IClock
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public DateTimeOffset UtcNow => _now;
    }
}
