namespace GameServer.Observability;

/// <summary>
/// Interval-derived telemetry for a log line or a live dashboard: per-second rates
/// computed from two cumulative <see cref="TelemetrySnapshot"/>s, plus a few cumulative
/// totals/high-water values. A pure function of its inputs (no clock, no I/O), so it is
/// trivially testable and is the single source of truth shared by the periodic log
/// flush and the live SSE feed — neither re-derives "what a telemetry interval means".
/// </summary>
public sealed record TelemetryRates(
    double Seconds,
    double MessagesInPerSecond,
    double MessagesOutPerSecond,
    double SnapshotsPerSecond,
    double EntitiesPerSecond,
    double CommandsAcceptedPerSecond,
    double CommandsRejectedPerSecond,
    double BackpressureRejectionsPerSecond,
    double TickMeanMs,
    double TickMaxMs,
    double CommandQueueDepthMean,
    long ConnectionsOpened,
    long ConnectionsDropped,
    long MissedTicks)
{
    /// <summary>The all-zero result, returned when there is no meaningful interval to divide by.</summary>
    public static readonly TelemetryRates Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>
    /// Computes interval telemetry from <paramref name="previous"/> to
    /// <paramref name="now"/> over <paramref name="seconds"/> of wall time. Counters
    /// become per-second rates; the entity measure becomes per-second sent volume; tick
    /// duration and queue depth become interval means; tick max is the cumulative
    /// high-water; connection and missed-tick fields are cumulative totals. A
    /// non-positive interval returns <see cref="Empty"/> rather than dividing by zero.
    /// </summary>
    public static TelemetryRates Between(TelemetrySnapshot previous, TelemetrySnapshot now, double seconds)
    {
        if (seconds <= 0)
        {
            return Empty;
        }

        double Rate(string metric) => (now.Counter(metric) - previous.Counter(metric)) / seconds;

        // Sum delta per second: for a measure where the meaningful quantity is the total
        // observed (e.g. entities actually sent), not the per-observation average.
        double PerSecondSum(string metric) => (now.Measure(metric).Sum - previous.Measure(metric).Sum) / seconds;

        // Average per observation within this interval (this interval's added count/sum),
        // so the value rises and falls with current behavior instead of an all-time mean.
        double IntervalMean(string metric)
        {
            var n = now.Measure(metric);
            var p = previous.Measure(metric);
            var count = n.Count - p.Count;
            return count > 0 ? (n.Sum - p.Sum) / count : 0;
        }

        return new TelemetryRates(
            seconds,
            Rate(TelemetryMetrics.MessagesIn),
            Rate(TelemetryMetrics.MessagesOut),
            Rate(TelemetryMetrics.SnapshotsEmitted),
            PerSecondSum(TelemetryMetrics.SnapshotEntities),
            Rate(TelemetryMetrics.CommandsAccepted),
            Rate(TelemetryMetrics.CommandsRejected),
            Rate(TelemetryMetrics.BackpressureRejections),
            IntervalMean(TelemetryMetrics.TickDurationMs),
            now.Measure(TelemetryMetrics.TickDurationMs).Max,
            IntervalMean(TelemetryMetrics.CommandQueueDepth),
            now.Counter(TelemetryMetrics.ConnectionsOpened),
            now.EventCount(TelemetryEvents.ConnectionDropped),
            now.Counter(TelemetryMetrics.MissedTicks));
    }
}
