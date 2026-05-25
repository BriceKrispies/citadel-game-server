using System.Collections.Concurrent;
using GameServer.Protocol;
using GameServer.Tenancy;

namespace GameServer.Matchmaking;

public sealed class MatchDirectorTests
{
    private static MatchScope Scope(string tenant = "acme", string game = "arena", int version = 1) =>
        new(new TenantId(tenant), new GameId(game), version);

    /// <summary>
    /// TicketToAssignment: a ticket flows pool -> match function -> evaluator -> assignment -> join
    /// token. Proves the whole happy path is wired and that the matched players land in one allocated
    /// room each holding a token minted for that exact room+player.
    /// </summary>
    [Fact]
    public void TicketToAssignment()
    {
        var clock = new FakeMonotonicClock();
        var registry = new TicketRegistry(clock);
        var scope = Scope();
        var pool = new Pool("default", scope);

        registry.Submit("t1", scope, new PlayerId("alice"), skill: 10);
        clock.Advance(TimeSpan.FromSeconds(1));
        registry.Submit("t2", scope, new PlayerId("bob"), skill: 12);

        var allocator = new FakeRoomAllocator();
        var tokens = new FakeJoinTokenIssuer();
        var director = new MatchDirector(new FixedSizeMatchFunction(matchSize: 2), new Evaluator(), allocator, tokens);

        var assignments = director.Cycle(pool, registry.ActiveTickets(scope));

        var match = Assert.Single(assignments);
        Assert.Equal(scope, match.Scope);
        Assert.Equal(2, match.Players.Count);
        Assert.Single(allocator.Allocations);

        // Every player got a token for THIS room, and both share one room (they were matched together).
        Assert.All(match.Players, p => Assert.Equal(match.RoomId, p.RoomId));
        Assert.All(match.Players, p => Assert.Contains(match.RoomId.Value, p.JoinToken));
        Assert.Contains(match.Players, p => p.PlayerId.Value == "alice");
        Assert.Contains(match.Players, p => p.PlayerId.Value == "bob");
    }

    /// <summary>
    /// A ticket the cluster cannot place is left UNassigned and stays claimable, so it retries — overload
    /// is shed cleanly, never silently dropped or stranded as "assigned but roomless".
    /// </summary>
    [Fact]
    public void ClusterAtCapacity_LeavesTicketsUnassignedAndRetryable()
    {
        var clock = new FakeMonotonicClock();
        var registry = new TicketRegistry(clock);
        var scope = Scope();
        var pool = new Pool("default", scope);
        registry.Submit("t1", scope, new PlayerId("a"));
        registry.Submit("t2", scope, new PlayerId("b"));

        var director = new MatchDirector(
            new FixedSizeMatchFunction(2), new Evaluator(), new FakeRoomAllocator(atCapacity: true), new FakeJoinTokenIssuer());

        var assignments = director.Cycle(pool, registry.ActiveTickets(scope));

        Assert.Empty(assignments);
        Assert.False(director.IsAssigned("t1"));
        Assert.False(director.IsAssigned("t2"));
    }

    /// <summary>
    /// Assignment race: two directors sharing one assigned-ticket store both try the same winning match
    /// concurrently. Exactly one assigns the ticket; the other loses the race. A ticket is NEVER
    /// double-assigned (two rooms for the same player).
    /// </summary>
    [Fact]
    public void ConcurrentDirectors_NeverDoubleAssignATicket()
    {
        var clock = new FakeMonotonicClock();
        var registry = new TicketRegistry(clock);
        var scope = Scope();
        var pool = new Pool("default", scope);
        registry.Submit("t1", scope, new PlayerId("a"));
        registry.Submit("t2", scope, new PlayerId("b"));

        // One shared claim set, two independent directors (as if two host instances raced).
        var shared = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var allocator = new FakeRoomAllocator();
        var tokens = new FakeJoinTokenIssuer();
        MatchDirector Make() => new(new FixedSizeMatchFunction(2), new Evaluator(), allocator, tokens, shared);

        var d1 = Make();
        var d2 = Make();
        var tickets = registry.ActiveTickets(scope);

        var results = new IReadOnlyList<MatchAssignment>[2];
        Parallel.Invoke(
            () => results[0] = d1.Cycle(pool, tickets),
            () => results[1] = d2.Cycle(pool, tickets));

        // Across BOTH directors, each ticket id appears in at most one assignment.
        var assignedTicketIds = results
            .SelectMany(r => r)
            .SelectMany(m => m.Players.Select(p => p.TicketId))
            .ToList();
        Assert.Equal(assignedTicketIds.Count, assignedTicketIds.Distinct().Count());

        // And the winning match (if any) was assigned exactly once, not twice.
        Assert.True(results[0].Count + results[1].Count <= 1);
    }
}
