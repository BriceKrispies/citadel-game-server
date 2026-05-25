using GameServer.Protocol;
using GameServer.Tenancy;
using Xunit;

namespace GameServer.Matchmaking;

public sealed class AssignmentStoreTests
{
    private static MatchScope Scope() => new(new TenantId("acme"), new GameId("arena"), 1);

    private static PlayerAssignment Assignment(string ticketId, string player = "p1") =>
        new(ticketId, new PlayerId(player), Scope(), new RoomId("room-1"), $"token-{ticketId}");

    [Fact]
    public void RecordedAssignment_IsFetchableByTicketId()
    {
        var store = new AssignmentStore(new FakeMonotonicClock());
        store.Record(Assignment("t1"));

        Assert.True(store.TryGet("t1", out var fetched));
        Assert.Equal("token-t1", fetched.JoinToken);
        Assert.Equal("room-1", fetched.RoomId.Value);
    }

    [Fact]
    public void UnknownTicket_IsNotFound()
    {
        var store = new AssignmentStore(new FakeMonotonicClock());
        Assert.False(store.TryGet("missing", out _));
    }

    [Fact]
    public void RecordingSameTicketTwice_KeepsTheFirstAssignment()
    {
        var store = new AssignmentStore(new FakeMonotonicClock());
        store.Record(Assignment("t1", "p1"));
        store.Record(new PlayerAssignment("t1", new PlayerId("p2"), Scope(), new RoomId("room-2"), "token-second"));

        Assert.True(store.TryGet("t1", out var fetched));
        Assert.Equal("token-t1", fetched.JoinToken); // immutable: first wins
    }

    [Fact]
    public void ExpiredAssignment_IsPruned_AndNotFetchable()
    {
        var clock = new FakeMonotonicClock();
        var store = new AssignmentStore(clock, ttlSeconds: 60);
        store.Record(Assignment("t1"));

        clock.Advance(TimeSpan.FromSeconds(61)); // past the TTL

        Assert.False(store.TryGet("t1", out _));
    }

    [Fact]
    public void Record_PrunesExpiredEntries_SoTheStoreSelfBounds()
    {
        var clock = new FakeMonotonicClock();
        var store = new AssignmentStore(clock, ttlSeconds: 10);

        // Record many short-lived assignments, advancing past the TTL between each. The store must not
        // grow unbounded — each record prunes everything older than the window.
        for (var i = 0; i < 100; i++)
        {
            store.Record(Assignment($"t{i}"));
            clock.Advance(TimeSpan.FromSeconds(11));
        }

        // After the loop only the final (just-recorded) entry can remain.
        Assert.True(store.Count <= 1);
    }

    [Fact]
    public void NonExpiredEntries_SurviveAcrossPrunes()
    {
        var clock = new FakeMonotonicClock();
        var store = new AssignmentStore(clock, ttlSeconds: 100);
        store.Record(Assignment("keep"));

        clock.Advance(TimeSpan.FromSeconds(10));
        store.Record(Assignment("also-keep"));
        store.PruneExpired();

        Assert.True(store.TryGet("keep", out _));
        Assert.True(store.TryGet("also-keep", out _));
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void ZeroOrNegativeTtl_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AssignmentStore(new FakeMonotonicClock(), ttlSeconds: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AssignmentStore(new FakeMonotonicClock(), ttlSeconds: -1));
    }
}
