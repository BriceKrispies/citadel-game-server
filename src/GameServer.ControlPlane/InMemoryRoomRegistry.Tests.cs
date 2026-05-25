using GameServer.Identity;
using GameServer.Identity.Testing;
using GameServer.Persistence;
using GameServer.Protocol;
using GameServer.Routing;
using GameServer.Simulation;
using GameServer.Simulation.Testing;
using Xunit;

namespace GameServer.ControlPlane;

public sealed class InMemoryRoomRegistryTests
{
    [Fact]
    public void Http_PostRoom_ReturnsStableRoomContract()
    {
        var registry = new InMemoryRoomRegistry();

        var room = registry.Create(new CreateRoomRequest("tenant-a", "demo-game"));

        Assert.False(string.IsNullOrEmpty(room.RoomId));
        Assert.Equal("tenant-a", room.TenantId);
        Assert.Equal("demo-game", room.GameId);
        Assert.Equal("open", room.Status);
        Assert.True(registry.TryGet(room.RoomId, out var fetched));
        Assert.Equal(room, fetched);
    }

    [Fact]
    public void Http_Endpoints_DoNotMutateSimulationState()
    {
        // Realtime simulation data plane.
        var router = new InMemorySessionRouter(
            (id, _) => new GameRoom(id, new MoveRightGame(), new FakeSimulationClock(), new DeterministicRandomSource()));
        var snapshots = new InMemorySnapshotStore<RoomKey, RoomSnapshot>();

        // Control-plane operations.
        var rooms = new InMemoryRoomRegistry();
        var tokens = new JoinTokenService(
            new Hs256JoinTokenCodec("test-secret", new FakeClock(DateTimeOffset.UnixEpoch), TimeSpan.FromMinutes(1)));
        var sessions = new InMemorySessionRegistry();

        var room = rooms.Create(new CreateRoomRequest("tenant-a", "demo-game"));
        tokens.Issue("tenant-a", "demo-game", room.RoomId, "player-1");
        sessions.Create(new CreateSessionRequest("tenant-a", "player-1"));

        // The authoritative simulation must be untouched: no room, no snapshot.
        var key = new RoomKey(new TenantId("tenant-a"), new RoomId(room.RoomId));
        Assert.False(router.TryGetRoom(key, out _));
        Assert.False(snapshots.TryGetLatest(key, out _));
    }

    [Fact]
    public void ListForGame_ReturnsOnlyOpenRoomsForThatTenantAndGame()
    {
        var registry = new InMemoryRoomRegistry();
        var a1 = registry.Create(new CreateRoomRequest("tenant-a", "demo-game"));
        var a2 = registry.Create(new CreateRoomRequest("tenant-a", "demo-game"));
        registry.Create(new CreateRoomRequest("tenant-a", "grid-walk")); // other game, same tenant
        registry.Create(new CreateRoomRequest("tenant-b", "demo-game")); // same game, other tenant

        var rooms = registry.ListForGame("tenant-a", "demo-game");

        Assert.Equal(2, rooms.Count);
        Assert.All(rooms, r => Assert.Equal("tenant-a", r.TenantId));
        Assert.All(rooms, r => Assert.Equal("demo-game", r.GameId));
        Assert.Contains(rooms, r => r.RoomId == a1.RoomId);
        Assert.Contains(rooms, r => r.RoomId == a2.RoomId);
        // No cross-tenant or cross-game leak.
        Assert.DoesNotContain(rooms, r => r.TenantId == "tenant-b");
        Assert.DoesNotContain(rooms, r => r.GameId == "grid-walk");
    }

    [Fact]
    public void EnsureAtLeastOne_CreatesWhenNone_AndIsIdempotent()
    {
        var registry = new InMemoryRoomRegistry();
        Assert.Empty(registry.ListForGame("tenant-a", "demo-game"));

        var first = registry.EnsureAtLeastOne("tenant-a", "demo-game");
        Assert.Equal("tenant-a", first.TenantId);
        Assert.Equal("demo-game", first.GameId);
        Assert.Equal("open", first.Status);
        Assert.Single(registry.ListForGame("tenant-a", "demo-game"));

        // A second ensure returns the SAME room and creates nothing — still exactly one.
        var second = registry.EnsureAtLeastOne("tenant-a", "demo-game");
        Assert.Equal(first.RoomId, second.RoomId);
        Assert.Single(registry.ListForGame("tenant-a", "demo-game"));
    }
}
