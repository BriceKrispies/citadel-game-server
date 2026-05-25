using GameServer.Protocol;

namespace GameServer.Matchmaking;

public sealed class EvaluatorTests
{
    private static MatchScope Scope() => new(new TenantId("acme"), new GameId("arena"), 1);

    private static MatchTicket Ticket(string id) => new(id, Scope(), new PlayerId(id), 0, 0);

    private static CandidateMatch Match(double quality, params string[] ids) =>
        new(Scope(), ids.Select(Ticket).ToList(), quality);

    [Fact]
    public void NonOverlappingCandidates_AllWin()
    {
        var winners = new Evaluator().Evaluate(new[] { Match(0, "a", "b"), Match(0, "c", "d") });
        Assert.Equal(2, winners.Count);
    }

    [Fact]
    public void OverlappingCandidates_HigherQualityWins_OtherDropped()
    {
        // Both want ticket "b"; the higher-quality match wins, the other is discarded.
        var winners = new Evaluator().Evaluate(new[]
        {
            Match(quality: 1, "a", "b"),
            Match(quality: 5, "b", "c"),
        });

        var winner = Assert.Single(winners);
        Assert.Equal(new[] { "b", "c" }, winner.TicketIds.OrderBy(x => x));
    }

    [Fact]
    public void AcrossAllWinners_NoTicketAppearsTwice()
    {
        var winners = new Evaluator().Evaluate(new[]
        {
            Match(3, "a", "b"),
            Match(2, "b", "c"),
            Match(1, "c", "d"),
        });

        var ids = winners.SelectMany(w => w.TicketIds).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Deterministic_TieBrokenByTicketIds()
    {
        var candidates = new[] { Match(0, "c", "d"), Match(0, "a", "b") };
        var first = new Evaluator().Evaluate(candidates).SelectMany(w => w.TicketIds).ToList();
        var second = new Evaluator().Evaluate(candidates).SelectMany(w => w.TicketIds).ToList();
        Assert.Equal(first, second);
    }
}
