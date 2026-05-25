using GameServer.Protocol;

namespace GameServer.Matchmaking;

public sealed class PoolTests
{
    private static MatchScope Scope(string tenant = "acme", string game = "arena", int version = 1) =>
        new(new TenantId(tenant), new GameId(game), version);

    private static MatchTicket Ticket(string id, MatchScope scope, int skill = 0) =>
        new(id, scope, new PlayerId(id), skill, EnqueuedSeconds: 0);

    /// <summary>
    /// PoolRules: a match function honors pool predicates — a ticket outside the predicate is filtered
    /// out before any match can form.
    /// </summary>
    [Fact]
    public void PoolRules()
    {
        var scope = Scope();
        var ranked = new Pool("ranked", scope, predicate: t => t.Attribute("mode") == "ranked");

        var inPool = Ticket("in", scope) with { Attributes = new Dictionary<string, string> { ["mode"] = "ranked" } };
        var outOfPool = Ticket("out", scope) with { Attributes = new Dictionary<string, string> { ["mode"] = "casual" } };

        Assert.True(ranked.Admits(inPool));
        Assert.False(ranked.Admits(outOfPool));

        var filtered = ranked.Filter(new[] { inPool, outOfPool }).ToList();
        Assert.Equal(new[] { "in" }, filtered.Select(t => t.TicketId));
    }

    [Fact]
    public void ScopeMismatch_IsNeverAdmitted_EvenWhenPredicateWouldAccept()
    {
        var pool = new Pool("default", Scope(version: 1), predicate: static _ => true);
        var crossVersion = Ticket("x", Scope(version: 2));

        // Predicate says yes, but a different scope is non-negotiably rejected.
        Assert.False(pool.Admits(crossVersion));
    }

    [Fact]
    public void PoolKey_IncludesScope_SoSameNameInDifferentScopesAreDifferentPools()
    {
        var a = new Pool("default", Scope(tenant: "acme"));
        var b = new Pool("default", Scope(tenant: "globex"));
        Assert.NotEqual(a.Key, b.Key);
    }

    [Fact]
    public void EmptyName_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new Pool(" ", Scope()));
    }
}
