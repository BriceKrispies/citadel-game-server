using Xunit;

namespace GameServer.Persistence;

public sealed class InMemorySnapshotHistoryStoreTests
{
    private sealed record Checkpoint(long Tick, string State);

    private static InMemorySnapshotHistoryStore<string, Checkpoint> NewStore() =>
        new(c => c.Tick);

    [Fact]
    public void Save_Then_TryGetLatest_ReturnsNewestCheckpoint()
    {
        var store = NewStore();
        store.Save("room", new Checkpoint(10, "a"));
        store.Save("room", new Checkpoint(20, "b"));

        Assert.True(store.TryGetLatest("room", out var latest));
        Assert.Equal(new Checkpoint(20, "b"), latest);
    }

    [Fact]
    public void Save_OutOfOrder_StillOrdersByTick()
    {
        var store = NewStore();
        store.Save("room", new Checkpoint(20, "b"));
        store.Save("room", new Checkpoint(10, "a"));

        Assert.Equal(new long[] { 10, 20 }, store.ListCheckpointTicks("room"));
        Assert.True(store.TryGetLatest("room", out var latest));
        Assert.Equal(20, latest.Tick);
    }

    [Fact]
    public void Save_AtSameTick_Replaces_NotDuplicates()
    {
        var store = NewStore();
        store.Save("room", new Checkpoint(10, "old"));
        store.Save("room", new Checkpoint(10, "new"));

        Assert.Equal(new long[] { 10 }, store.ListCheckpointTicks("room"));
        Assert.True(store.TryGetLatestAtOrBefore("room", 10, out var at));
        Assert.Equal("new", at.State);
    }

    [Fact]
    public void TryGetLatestAtOrBefore_ReturnsTheFloorCheckpoint()
    {
        var store = NewStore();
        store.Save("room", new Checkpoint(10, "a"));
        store.Save("room", new Checkpoint(20, "b"));
        store.Save("room", new Checkpoint(30, "c"));

        Assert.True(store.TryGetLatestAtOrBefore("room", 25, out var at));
        Assert.Equal(new Checkpoint(20, "b"), at); // newest at-or-before 25

        Assert.True(store.TryGetLatestAtOrBefore("room", 30, out var exact));
        Assert.Equal(30, exact.Tick); // exact match is at-or-before
    }

    [Fact]
    public void TryGetLatestAtOrBefore_BelowEverything_Misses()
    {
        var store = NewStore();
        store.Save("room", new Checkpoint(10, "a"));

        Assert.False(store.TryGetLatestAtOrBefore("room", 5, out _));
    }

    [Fact]
    public void TryGetLatest_And_AtOrBefore_MissUnknownKey()
    {
        var store = NewStore();

        Assert.False(store.TryGetLatest("nope", out _));
        Assert.False(store.TryGetLatestAtOrBefore("nope", 100, out _));
        Assert.Empty(store.ListCheckpointTicks("nope"));
    }

    [Fact]
    public void PruneThrough_KeepsFloorAtOrBelowWatermark_DropsOlder_KeepsNewer()
    {
        var store = NewStore();
        foreach (var tick in new long[] { 10, 20, 30, 40 })
        {
            store.Save("room", new Checkpoint(tick, $"c{tick}"));
        }

        // Horizon watermark 25: 20 is the floor (newest at-or-below 25). Drop 10, keep 20/30/40 —
        // 20 is still needed as the restore base for rewinding to any tick in [25, present].
        store.PruneThrough("room", throughTick: 25);

        Assert.Equal(new long[] { 20, 30, 40 }, store.ListCheckpointTicks("room"));
        Assert.True(store.TryGetLatestAtOrBefore("room", 25, out var floor));
        Assert.Equal(20, floor.Tick);
    }

    [Fact]
    public void PruneThrough_AboveAll_KeepsOnlyTheNewest()
    {
        var store = NewStore();
        foreach (var tick in new long[] { 10, 20, 30 })
        {
            store.Save("room", new Checkpoint(tick, $"c{tick}"));
        }

        store.PruneThrough("room", throughTick: 100);

        // Everything is at or below the watermark, but the newest must survive as the restore base.
        Assert.Equal(new long[] { 30 }, store.ListCheckpointTicks("room"));
    }

    [Fact]
    public void Histories_AreIsolated_PerKey()
    {
        var store = NewStore();
        store.Save("room-1", new Checkpoint(10, "x"));
        store.Save("room-2", new Checkpoint(99, "y"));

        Assert.Equal(new long[] { 10 }, store.ListCheckpointTicks("room-1"));
        Assert.Equal(new long[] { 99 }, store.ListCheckpointTicks("room-2"));
    }
}
