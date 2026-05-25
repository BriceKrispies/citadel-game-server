using GameServer.Protocol;

namespace GameServer.Matchmaking;

public sealed class CandidateMatchTests
{
    private static MatchScope Scope() => new(new TenantId("acme"), new GameId("arena"), 1);

    private static MatchTicket Ticket(string id, MatchScope scope) =>
        new(id, scope, new PlayerId(id), 0, 0);

    [Fact]
    public void HoldsTicketsAndExposesIds()
    {
        var scope = Scope();
        var match = new CandidateMatch(scope, new[] { Ticket("a", scope), Ticket("b", scope) }, quality: 3);
        Assert.Equal(scope, match.Scope);
        Assert.Equal(3, match.Quality);
        Assert.Equal(new[] { "a", "b" }, match.TicketIds);
    }

    [Fact]
    public void EmptyTickets_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new CandidateMatch(Scope(), Array.Empty<MatchTicket>()));
    }

    [Fact]
    public void TicketFromAnotherScope_IsRejected()
    {
        var scope = Scope();
        var other = new MatchScope(new TenantId("acme"), new GameId("arena"), 2);
        Assert.Throws<ArgumentException>(() => new CandidateMatch(scope, new[] { Ticket("a", scope), Ticket("b", other) }));
    }
}
