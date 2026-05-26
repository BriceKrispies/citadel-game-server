using Xunit;

namespace GameServer.Persistence;

public sealed class InMemoryEventLogTests
{
    [Fact]
    public void Append_PreservesOrder_PerKey()
    {
        var log = new InMemoryEventLog<string, string>();
        log.Append("room", "a");
        log.Append("room", "b");

        Assert.Equal(new[] { "a", "b" }, log.Read("room"));
    }

    [Fact]
    public void Read_UnknownKey_IsEmpty()
    {
        var log = new InMemoryEventLog<string, string>();

        Assert.Empty(log.Read("nope"));
    }

    [Fact]
    public void Streams_AreIsolated_PerKey()
    {
        var log = new InMemoryEventLog<string, string>();
        log.Append("room-1", "x");
        log.Append("room-2", "y");

        Assert.Equal(new[] { "x" }, log.Read("room-1"));
        Assert.Equal(new[] { "y" }, log.Read("room-2"));
    }

    [Fact]
    public void TruncateThrough_DropsEventsAtOrBelowTheWatermark_KeepingTheSuffix()
    {
        // Events carry their own sequence (here the int value); compact through 3.
        var log = new InMemoryEventLog<string, int>(e => e);
        foreach (var seq in new[] { 1, 2, 3, 4, 5 })
        {
            log.Append("room", seq);
        }

        log.TruncateThrough("room", throughSequence: 3);

        Assert.Equal(new[] { 4, 5 }, log.Read("room"));
    }

    [Fact]
    public void TruncateThrough_BelowEverything_KeepsAll_AboveEverything_KeepsNone()
    {
        var log = new InMemoryEventLog<string, int>(e => e);
        foreach (var seq in new[] { 10, 20, 30 })
        {
            log.Append("room", seq);
        }

        log.TruncateThrough("room", throughSequence: 5);
        Assert.Equal(new[] { 10, 20, 30 }, log.Read("room"));

        log.TruncateThrough("room", throughSequence: 100);
        Assert.Empty(log.Read("room"));
    }

    [Fact]
    public void TruncateThrough_WithoutSequenceSelector_Throws()
    {
        var log = new InMemoryEventLog<string, int>();
        log.Append("room", 1);

        Assert.Throws<InvalidOperationException>(() => log.TruncateThrough("room", 0));
    }

    [Fact]
    public void ReadRange_ReturnsEvents_AboveLowExclusive_ThroughHighInclusive()
    {
        var log = new InMemoryEventLog<string, int>(e => e);
        foreach (var seq in new[] { 1, 2, 3, 4, 5 })
        {
            log.Append("room", seq);
        }

        // Half-open low (exclude 2), inclusive high (include 4).
        Assert.Equal(new[] { 3, 4 }, log.ReadRange("room", fromExclusive: 2, toInclusive: 4));
    }

    [Fact]
    public void ReadRange_EmptyWindow_IsEmpty_AndUnknownKey_IsEmpty()
    {
        var log = new InMemoryEventLog<string, int>(e => e);
        foreach (var seq in new[] { 1, 2, 3 })
        {
            log.Append("room", seq);
        }

        Assert.Empty(log.ReadRange("room", fromExclusive: 3, toInclusive: 3));
        Assert.Empty(log.ReadRange("nope", fromExclusive: 0, toInclusive: 100));
    }

    [Fact]
    public void DiscardAfter_DropsEventsStrictlyAboveTheTick_KeepingThePrefix()
    {
        var log = new InMemoryEventLog<string, int>(e => e);
        foreach (var seq in new[] { 1, 2, 3, 4, 5 })
        {
            log.Append("room", seq);
        }

        log.DiscardAfter("room", tick: 3);

        Assert.Equal(new[] { 1, 2, 3 }, log.Read("room"));
    }

    [Fact]
    public void DiscardAfter_ThenAppend_ExtendsAFreshTimeline()
    {
        var log = new InMemoryEventLog<string, int>(e => e);
        foreach (var seq in new[] { 1, 2, 3, 4, 5 })
        {
            log.Append("room", seq);
        }

        log.DiscardAfter("room", tick: 2);
        log.Append("room", 3); // a new, different future after the fork

        Assert.Equal(new[] { 1, 2, 3 }, log.Read("room"));
    }

    [Fact]
    public void ReadRange_And_DiscardAfter_WithoutSequenceSelector_Throw()
    {
        var log = new InMemoryEventLog<string, int>();
        log.Append("room", 1);

        Assert.Throws<InvalidOperationException>(() => log.ReadRange("room", 0, 10));
        Assert.Throws<InvalidOperationException>(() => log.DiscardAfter("room", 0));
    }
}
