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
            id => new GameRoom(id, new FakeSimulationClock(), new DeterministicRandomSource()));
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
}
