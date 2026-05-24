using GameServer.Protocol;
using GameServer.Replication;
using GameServer.Simulation;
using GameServer.Tenancy;
using Xunit;

namespace GameServer.Routing;

public sealed class InMemorySessionRouterTests
{
    private static readonly TenantContext TenantA = new(new TenantId("tenant-a"), "Tenant A");
    private static readonly TenantContext TenantB = new(new TenantId("tenant-b"), "Tenant B");
    private static readonly GameId Game = new("demo");

    // A minimal stand-in room: routing tests care about placement/keying, not simulation.
    private static InMemorySessionRouter NewRouter() =>
        new((roomId, _) => new StubRoom(roomId));

    [Fact]
    public void CreateSession_IssuesDeterministicIncrementingIds()
    {
        var router = NewRouter();

        Assert.Equal(new SessionId("session-1"), router.CreateSession(TenantA).Id);
        Assert.Equal(new SessionId("session-2"), router.CreateSession(TenantA).Id);
    }

    [Fact]
    public void GetOrCreateRoom_ReturnsSameInstance_ForSameTenantAndRoom()
    {
        var router = NewRouter();

        var first = router.GetOrCreateRoom(TenantA, new RoomId("arena"), Game);
        var second = router.GetOrCreateRoom(TenantA, new RoomId("arena"), Game);

        Assert.Same(first, second);
    }

    [Fact]
    public void SameRoomId_AcrossTenants_ResolvesToDistinctRooms()
    {
        var router = NewRouter();

        var roomA = router.GetOrCreateRoom(TenantA, new RoomId("arena"), Game);
        var roomB = router.GetOrCreateRoom(TenantB, new RoomId("arena"), Game);

        Assert.NotSame(roomA, roomB);
    }

    [Fact]
    public void TryGetRoom_FindsPlacedRoom_AndMissesUnplaced()
    {
        var router = NewRouter();
        router.GetOrCreateRoom(TenantA, new RoomId("arena"), Game);

        Assert.True(router.TryGetRoom(new RoomKey(TenantA.TenantId, new RoomId("arena")), out _));
        Assert.False(router.TryGetRoom(new RoomKey(TenantB.TenantId, new RoomId("arena")), out _));
    }

    private sealed class StubRoom : IGameRoom
    {
        public StubRoom(RoomId id) => Id = id;

        public RoomId Id { get; }

        public int QueueDepth => 0;

        public void Join(PlayerId player) { }

        public bool HasPlayer(PlayerId player) => false;

        public CommandAdmission TryEnqueue(PlayerId player, string command, long sequence) => CommandAdmission.Accepted;

        public TickResult Tick() =>
            new(new RoomSnapshot(0, Array.Empty<byte>()), Array.Empty<RoomEvent>());

        public IReadOnlyList<EntitySnapshot> Project() => Array.Empty<EntitySnapshot>();

        public RoomSnapshot Snapshot() => new(0, Array.Empty<byte>());

        public void RestoreFrom(RoomSnapshot snapshot) { }

        public bool ApplyRecoveredEvent(RoomEvent recoveredEvent) => true;
    }
}
