using GameServer.Protocol;
using GameServer.Tenancy;

namespace GameServer.Matchmaking;

public sealed class MatchScopeTests
{
    private static MatchScope Scope(string tenant, string game, int version) =>
        new(new TenantId(tenant), new GameId(game), version);

    [Fact]
    public void Scopes_DifferingInAnyDimension_AreNotEqual()
    {
        var baseline = Scope("acme", "arena", 1);
        Assert.NotEqual(baseline, Scope("globex", "arena", 1)); // tenant
        Assert.NotEqual(baseline, Scope("acme", "puzzle", 1));  // game
        Assert.NotEqual(baseline, Scope("acme", "arena", 2));   // version
        Assert.Equal(baseline, Scope("acme", "arena", 1));      // identical
    }

    /// <summary>
    /// MatchmakingTenantIsolation: tickets NEVER match across tenants, games, or versions. We force the
    /// adversarial case — one ticket from each of four distinct scopes, all in the same registry, run
    /// through a director — and assert that no candidate or assignment ever spans two scopes. Isolation
    /// is enforced by the scope being part of the pool key (and validated again in CandidateMatch), not
    /// by a late filter that could be skipped.
    /// </summary>
    [Fact]
    public void MatchmakingTenantIsolation()
    {
        var clock = new FakeMonotonicClock();
        var registry = new TicketRegistry(clock);

        var acmeV1 = Scope("acme", "arena", 1);
        var acmeV2 = Scope("acme", "arena", 2);   // same tenant+game, different VERSION
        var globexV1 = Scope("globex", "arena", 1); // different TENANT, same game+version
        var acmePuzzle = Scope("acme", "puzzle", 1); // same tenant, different GAME

        // Two tickets per scope so a 2-player match COULD form within a scope, but never across.
        foreach (var scope in new[] { acmeV1, acmeV2, globexV1, acmePuzzle })
        {
            registry.Submit($"{scope}#1", scope, new PlayerId("p1"));
            registry.Submit($"{scope}#2", scope, new PlayerId("p2"));
        }

        var allocator = new FakeRoomAllocator();
        var tokens = new FakeJoinTokenIssuer();
        var fn = new FixedSizeMatchFunction(matchSize: 2);
        var director = new MatchDirector(fn, new Evaluator(), allocator, tokens);

        foreach (var scope in new[] { acmeV1, acmeV2, globexV1, acmePuzzle })
        {
            // The pool for this scope only ever reads this scope's tickets (ticket leakage is impossible).
            var pool = new Pool("default", scope);
            var visible = registry.ActiveTickets(scope);
            Assert.All(visible, t => Assert.Equal(scope, t.Scope));

            var assignments = director.Cycle(pool, visible);

            // A match formed (two same-scope tickets), but every player in it shares the one scope.
            var match = Assert.Single(assignments);
            Assert.All(match.Players, p => Assert.Equal(scope, p.Scope));
            Assert.All(match.Players, p => Assert.Contains(scope.TenantId.Value, p.JoinToken));
        }

        // Every issued token is scoped to exactly the tenant/game/version it was matched in.
        foreach (var (issuedScope, room, _) in tokens.Issued)
        {
            Assert.Contains(allocator.Allocations, a => a.Scope == issuedScope && a.Room == room);
        }
    }

    /// <summary>
    /// Even if a caller hands a match function a poisoned ticket list mixing scopes (a bug upstream), a
    /// candidate that crosses scopes cannot be constructed — the type boundary rejects it.
    /// </summary>
    [Fact]
    public void CandidateMatch_CannotBeConstructedAcrossScopes()
    {
        var acme = Scope("acme", "arena", 1);
        var globex = Scope("globex", "arena", 1);
        var tickets = new MatchTicket[]
        {
            new("a", acme, new PlayerId("a"), 0, 0),
            new("b", globex, new PlayerId("b"), 0, 0),
        };

        Assert.Throws<ArgumentException>(() => new CandidateMatch(acme, tickets));
    }
}
