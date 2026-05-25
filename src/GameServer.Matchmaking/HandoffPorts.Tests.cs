using GameServer.Protocol;

namespace GameServer.Matchmaking;

public sealed class HandoffPortsTests
{
    private static MatchScope Scope() => new(new TenantId("acme"), new GameId("arena"), 1);

    [Fact]
    public void FakeRoomAllocator_HandsOutDistinctRooms_AndCanReportCapacity()
    {
        var ok = new FakeRoomAllocator();
        var r1 = ok.Allocate(Scope());
        var r2 = ok.Allocate(Scope());
        Assert.NotNull(r1);
        Assert.NotNull(r2);
        Assert.NotEqual(r1, r2);
        Assert.Equal(2, ok.Allocations.Count);

        var full = new FakeRoomAllocator(atCapacity: true);
        Assert.Null(full.Allocate(Scope()));
    }

    [Fact]
    public void FakeJoinTokenIssuer_EncodesScopeRoomPlayer()
    {
        var issuer = new FakeJoinTokenIssuer();
        var token = issuer.IssueJoinToken(Scope(), new RoomId("room-7"), new PlayerId("alice"));
        Assert.Contains("acme", token);
        Assert.Contains("arena", token);
        Assert.Contains("room-7", token);
        Assert.Contains("alice", token);
        Assert.Single(issuer.Issued);
    }
}
