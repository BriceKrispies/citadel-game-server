using Xunit;

namespace GameServer.Observability;

public sealed class RoomScopedMetricsTests
{
    [Fact]
    public void Hottest_IdentifiesThePeakRoom_AcrossManyCheapOnes()
    {
        var metrics = new RoomScopedMetrics();
        foreach (var i in Enumerable.Range(0, 20))
        {
            metrics.Record($"room-{i}", 1.0); // cheap rooms
        }

        metrics.Record("room-hot", 50.0); // one expensive room

        var top = metrics.Hottest(1);
        Assert.Single(top);
        Assert.Equal("room-hot", top[0].Room);
        Assert.Equal(50.0, top[0].MaxMs);
    }

    [Fact]
    public void Record_AggregatesMeanAndMax_PerRoom()
    {
        var metrics = new RoomScopedMetrics();
        metrics.Record("r", 10);
        metrics.Record("r", 20);
        metrics.Record("r", 30);

        var stat = metrics.Hottest(1).Single();
        Assert.Equal(3, stat.Count);
        Assert.Equal(20, stat.MeanMs);
        Assert.Equal(30, stat.MaxMs);
    }

    [Fact]
    public void Cardinality_IsBounded_AndEvictsTheColdest_KeepingHotRooms()
    {
        var metrics = new RoomScopedMetrics(maxRooms: 2);
        metrics.Record("hot", 100);
        metrics.Record("warm", 10);

        // Tracking a third room while at the cap of 2 evicts the coldest currently tracked
        // (warm, peak 10), not the new arrival. The hot room is always retained.
        metrics.Record("cold", 1);
        Assert.Equal(2, metrics.TrackedRooms);
        Assert.Equal(1, metrics.EvictedRooms);
        var rooms = metrics.Hottest(5).Select(s => s.Room).ToList();
        Assert.Contains("hot", rooms);
        Assert.DoesNotContain("warm", rooms);
    }

    [Fact]
    public void Construction_WithNonPositiveCap_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RoomScopedMetrics(maxRooms: 0));
    }
}
