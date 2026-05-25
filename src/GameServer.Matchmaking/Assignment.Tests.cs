using GameServer.Protocol;

namespace GameServer.Matchmaking;

public sealed class AssignmentTests
{
    private static MatchScope Scope() => new(new TenantId("acme"), new GameId("arena"), 1);

    [Fact]
    public void MatchAssignment_CarriesRoomAndPerPlayerTokens()
    {
        var scope = Scope();
        var room = new RoomId("room-1");
        var players = new[]
        {
            new PlayerAssignment("t1", new PlayerId("a"), scope, room, "tokenA"),
            new PlayerAssignment("t2", new PlayerId("b"), scope, room, "tokenB"),
        };

        var match = new MatchAssignment(scope, room, players);

        Assert.Equal(room, match.RoomId);
        Assert.Equal(2, match.Players.Count);
        Assert.All(match.Players, p => Assert.Equal(room, p.RoomId));
        Assert.Equal("tokenA", match.Players[0].JoinToken);
    }
}
