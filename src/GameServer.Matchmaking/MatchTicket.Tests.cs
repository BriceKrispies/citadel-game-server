using GameServer.Protocol;

namespace GameServer.Matchmaking;

public sealed class MatchTicketTests
{
    private static MatchScope Scope() => new(new TenantId("acme"), new GameId("arena"), 1);

    [Fact]
    public void Attribute_ReturnsValue_WhenPresent()
    {
        var ticket = new MatchTicket("t", Scope(), new PlayerId("p"), 0, 0,
            new Dictionary<string, string> { ["mode"] = "ranked" });
        Assert.Equal("ranked", ticket.Attribute("mode"));
    }

    [Fact]
    public void Attribute_ReturnsNull_WhenAbsentOrNoAttributes()
    {
        var withAttrs = new MatchTicket("t", Scope(), new PlayerId("p"), 0, 0,
            new Dictionary<string, string> { ["mode"] = "ranked" });
        Assert.Null(withAttrs.Attribute("missing"));

        var noAttrs = new MatchTicket("t", Scope(), new PlayerId("p"), 0, 0);
        Assert.Null(noAttrs.Attribute("mode"));
    }
}
