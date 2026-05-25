using GameServer.Protocol;

namespace GameServer.Matchmaking;

public sealed class FixedSizeMatchFunctionTests
{
    private static MatchScope Scope() => new(new TenantId("acme"), new GameId("arena"), 1);

    private static MatchTicket Ticket(string id, double enqueued, int skill = 0) =>
        new(id, Scope(), new PlayerId(id), skill, enqueued);

    [Fact]
    public void FormsFixedSizeMatches_OldestFirst()
    {
        var fn = new FixedSizeMatchFunction(matchSize: 2);
        var tickets = new[]
        {
            Ticket("c", enqueued: 3),
            Ticket("a", enqueued: 1),
            Ticket("b", enqueued: 2),
        };

        var matches = fn.Form(Scope(), tickets);

        // Oldest two (a@1, b@2) form the first match; c is left waiting (odd one out).
        var match = Assert.Single(matches);
        Assert.Equal(new[] { "a", "b" }, match.TicketIds.OrderBy(x => x));
    }

    [Fact]
    public void DoesNotFormMatch_WhenNotEnoughTickets()
    {
        var fn = new FixedSizeMatchFunction(matchSize: 2);
        Assert.Empty(fn.Form(Scope(), new[] { Ticket("a", 1) }));
    }

    [Fact]
    public void RespectsSkillBand_LeavingOutOfBandTicketsUnmatched()
    {
        var fn = new FixedSizeMatchFunction(matchSize: 2, skillBandWidth: 10);
        var tickets = new[]
        {
            Ticket("low", enqueued: 1, skill: 0),
            Ticket("high", enqueued: 2, skill: 100), // far out of band from "low"
        };

        // No pair within 10 skill of each other -> no match forms; they wait.
        Assert.Empty(fn.Form(Scope(), tickets));
    }

    [Fact]
    public void Deterministic_SameInputSameOutput()
    {
        var fn = new FixedSizeMatchFunction(matchSize: 2);
        var tickets = new[] { Ticket("a", 1), Ticket("b", 2), Ticket("c", 3), Ticket("d", 4) };

        var first = fn.Form(Scope(), tickets).Select(m => string.Join(",", m.TicketIds)).ToList();
        var second = fn.Form(Scope(), tickets).Select(m => string.Join(",", m.TicketIds)).ToList();
        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidMatchSize_IsRejected(int size)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FixedSizeMatchFunction(size));
    }
}
