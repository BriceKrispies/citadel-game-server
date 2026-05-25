using GameServer.Protocol;
using GameServer.Tenancy;

namespace GameServer.Matchmaking;

public sealed class TicketRegistryTests
{
    private static MatchScope Scope(string tenant = "acme", int version = 1) =>
        new(new TenantId(tenant), new GameId("arena"), version);

    [Fact]
    public void Submit_StampsEnqueueTimeFromTheClock()
    {
        var clock = new FakeMonotonicClock();
        var registry = new TicketRegistry(clock);
        clock.Advance(TimeSpan.FromSeconds(42));

        var ticket = registry.Submit("t1", Scope(), new PlayerId("p"));

        Assert.Equal(42, ticket.EnqueuedSeconds);
    }

    [Fact]
    public void ActiveTickets_NeverReturnsAnotherScopesTickets()
    {
        var registry = new TicketRegistry(new FakeMonotonicClock());
        registry.Submit("acme", Scope(tenant: "acme"), new PlayerId("a"));
        registry.Submit("globex", Scope(tenant: "globex"), new PlayerId("g"));

        var acmeTickets = registry.ActiveTickets(Scope(tenant: "acme"));
        Assert.Equal(new[] { "acme" }, acmeTickets.Select(t => t.TicketId));
    }

    [Fact]
    public void Remove_StopsTicketsFromBeingMatchedAgain()
    {
        var registry = new TicketRegistry(new FakeMonotonicClock());
        var scope = Scope();
        registry.Submit("t1", scope, new PlayerId("a"));
        registry.Submit("t2", scope, new PlayerId("b"));

        registry.Remove(scope, new[] { "t1" });

        Assert.Equal(new[] { "t2" }, registry.ActiveTickets(scope).Select(t => t.TicketId));
    }

    [Fact]
    public void ActiveTickets_ForUnknownScope_IsEmpty()
    {
        var registry = new TicketRegistry(new FakeMonotonicClock());
        Assert.Empty(registry.ActiveTickets(Scope(tenant: "nobody")));
    }
}
