using Xunit;

namespace GameServer.Observability;

public sealed class AggregatingTelemetrySinkTests
{
    [Fact]
    public void Increment_AggregatesCountsByName()
    {
        var sink = new AggregatingTelemetrySink();

        sink.Increment("messages_out");
        sink.Increment("messages_out");
        sink.Increment("messages_out");

        Assert.Equal(3, sink.Snapshot().Counter("messages_out"));
    }

    [Fact]
    public void Measure_AggregatesCountSumMinMax()
    {
        var sink = new AggregatingTelemetrySink();

        sink.Measure("tick_duration_ms", 5);
        sink.Measure("tick_duration_ms", 15);
        sink.Measure("tick_duration_ms", 10);

        var stats = sink.Snapshot().Measure("tick_duration_ms");
        Assert.Equal(3, stats.Count);
        Assert.Equal(30, stats.Sum);
        Assert.Equal(5, stats.Min);
        Assert.Equal(15, stats.Max);
        Assert.Equal(10, stats.Mean);
    }

    [Fact]
    public void Event_AggregatesCountsByName()
    {
        var sink = new AggregatingTelemetrySink();

        sink.Event("connection_opened");
        sink.Event("connection_opened");

        Assert.Equal(2, sink.Snapshot().EventCount("connection_opened"));
    }

    [Fact]
    public void Snapshot_IsAStableCopy()
    {
        var sink = new AggregatingTelemetrySink();
        sink.Increment("messages_in");

        var snapshot = sink.Snapshot();
        sink.Increment("messages_in"); // mutate after snapshotting

        Assert.Equal(1, snapshot.Counter("messages_in")); // snapshot is unaffected
        Assert.Equal(2, sink.Snapshot().Counter("messages_in"));
    }
}
