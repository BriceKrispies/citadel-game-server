using Xunit;

namespace GameServer.Observability;

/// <summary>
/// Tests the pure interval-rate computation that backs both the periodic log flush and
/// the live telemetry feed. All cases are deterministic: two hand-built cumulative
/// snapshots and a known elapsed interval in, derived rates out — no clock, no I/O.
/// </summary>
public sealed class TelemetryRatesTests
{
    private static TelemetrySnapshot Snapshot(
        Dictionary<string, long>? counters = null,
        Dictionary<string, MeasureStats>? measures = null,
        Dictionary<string, long>? events = null) =>
        new(counters ?? new(), measures ?? new(), events ?? new());

    [Fact]
    public void Between_ComputesCounterRatesOverTheInterval()
    {
        var previous = Snapshot(counters: new()
        {
            [TelemetryMetrics.MessagesOut] = 100,
            [TelemetryMetrics.CommandsAccepted] = 10,
            [TelemetryMetrics.BackpressureRejections] = 0,
        });
        var now = Snapshot(counters: new()
        {
            [TelemetryMetrics.MessagesOut] = 300,  // +200 over 2s -> 100/s
            [TelemetryMetrics.CommandsAccepted] = 30, // +20 -> 10/s
            [TelemetryMetrics.BackpressureRejections] = 4, // +4 -> 2/s
        });

        var rates = TelemetryRates.Between(previous, now, seconds: 2);

        Assert.Equal(100, rates.MessagesOutPerSecond);
        Assert.Equal(10, rates.CommandsAcceptedPerSecond);
        Assert.Equal(2, rates.BackpressureRejectionsPerSecond);
    }

    [Fact]
    public void Between_TurnsMeasuredEntitySumIntoPerSecondVolume()
    {
        // SnapshotEntities is a measure; the dashboard wants entities actually sent per
        // second (the number delta/interest/budget reduce), i.e. the SUM delta / seconds.
        var previous = Snapshot(measures: new()
        {
            [TelemetryMetrics.SnapshotEntities] = new MeasureStats(Count: 50, Sum: 0, Min: 1, Max: 1),
        });
        var now = Snapshot(measures: new()
        {
            [TelemetryMetrics.SnapshotEntities] = new MeasureStats(Count: 100, Sum: 1000, Min: 1, Max: 1),
        });

        var rates = TelemetryRates.Between(previous, now, seconds: 2);

        Assert.Equal(500, rates.EntitiesPerSecond); // 1000 entities sent over 2s
    }

    [Fact]
    public void Between_ReportsIntervalMeanForTickDuration_AndCumulativeMax()
    {
        var previous = Snapshot(measures: new()
        {
            [TelemetryMetrics.TickDurationMs] = new MeasureStats(Count: 0, Sum: 0, Min: 0, Max: 0),
        });
        var now = Snapshot(measures: new()
        {
            // 10 ticks this interval totaling 50ms -> mean 5ms; cumulative high-water 8ms.
            [TelemetryMetrics.TickDurationMs] = new MeasureStats(Count: 10, Sum: 50, Min: 1, Max: 8),
        });

        var rates = TelemetryRates.Between(previous, now, seconds: 1);

        Assert.Equal(5.0, rates.TickMeanMs);
        Assert.Equal(8.0, rates.TickMaxMs);
    }

    [Fact]
    public void Between_ReportsConnectionAndMissedTickTotals_AsCumulative()
    {
        var now = Snapshot(
            counters: new()
            {
                [TelemetryMetrics.ConnectionsOpened] = 42,
                [TelemetryMetrics.MissedTicks] = 3,
            },
            events: new() { [TelemetryEvents.ConnectionDropped] = 5 });

        var rates = TelemetryRates.Between(Snapshot(), now, seconds: 1);

        Assert.Equal(42, rates.ConnectionsOpened);
        Assert.Equal(5, rates.ConnectionsDropped);
        Assert.Equal(3, rates.MissedTicks);
    }

    [Fact]
    public void Between_NonPositiveInterval_YieldsEmpty_WithoutDividingByZero()
    {
        var now = Snapshot(counters: new() { [TelemetryMetrics.MessagesOut] = 1000 });

        Assert.Equal(TelemetryRates.Empty, TelemetryRates.Between(Snapshot(), now, seconds: 0));
        Assert.Equal(TelemetryRates.Empty, TelemetryRates.Between(Snapshot(), now, seconds: -1));
    }

    [Fact]
    public void Between_MissingMetrics_AreZero_NotNaN()
    {
        var rates = TelemetryRates.Between(Snapshot(), Snapshot(), seconds: 1);

        Assert.Equal(0, rates.MessagesOutPerSecond);
        Assert.Equal(0, rates.EntitiesPerSecond);
        Assert.Equal(0, rates.TickMeanMs);
        Assert.Equal(0, rates.CommandQueueDepthMean);
    }
}
