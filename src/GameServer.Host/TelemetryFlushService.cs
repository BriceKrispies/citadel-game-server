using System.Diagnostics;
using GameServer.Observability;

namespace GameServer.Host;

/// <summary>
/// Periodically flushes the aggregated telemetry as a compact rolling summary:
/// per-interval message/snapshot rates, tick-duration mean/max, missed ticks, and
/// process CPU/memory. The hot path only accumulates counters; this loop is the only
/// thing that reads and reports, so telemetry never amplifies the realtime load.
/// </summary>
public sealed class TelemetryFlushService : BackgroundService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    private readonly AggregatingTelemetrySink _sink;
    private readonly ILogger<TelemetryFlushService> _logger;
    private readonly Process _process = Process.GetCurrentProcess();

    private TelemetrySnapshot _previous = new(
        new Dictionary<string, long>(), new Dictionary<string, MeasureStats>(), new Dictionary<string, long>());
    private TimeSpan _previousCpu;
    private long _previousWall;

    public TelemetryFlushService(AggregatingTelemetrySink sink, ILogger<TelemetryFlushService> logger)
    {
        _sink = sink;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _previousCpu = _process.TotalProcessorTime;
        _previousWall = Stopwatch.GetTimestamp();
        using var timer = new PeriodicTimer(FlushInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            Flush();
        }
    }

    private void Flush()
    {
        var now = _sink.Snapshot();
        var wallNow = Stopwatch.GetTimestamp();
        var seconds = Stopwatch.GetElapsedTime(_previousWall, wallNow).TotalSeconds;
        if (seconds <= 0)
        {
            return;
        }

        double Rate(string metric) => (now.Counter(metric) - _previous.Counter(metric)) / seconds;

        var tickNow = now.Measure(TelemetryMetrics.TickDurationMs);
        var tickPrev = _previous.Measure(TelemetryMetrics.TickDurationMs);
        var tickIntervalCount = tickNow.Count - tickPrev.Count;
        var tickMeanMs = tickIntervalCount > 0 ? (tickNow.Sum - tickPrev.Sum) / tickIntervalCount : 0;

        // Replicated payload volume: entities actually sent per second. This is what
        // delta/interest/budget reduce (message count can stay flat while this drops).
        var entitiesNow = now.Measure(TelemetryMetrics.SnapshotEntities);
        var entitiesPrev = _previous.Measure(TelemetryMetrics.SnapshotEntities);
        var entitiesPerSecond = (entitiesNow.Sum - entitiesPrev.Sum) / seconds;

        var cpuNow = _process.TotalProcessorTime;
        var cpuPercent = (cpuNow - _previousCpu).TotalSeconds / (seconds * Environment.ProcessorCount) * 100.0;
        var memMb = _process.WorkingSet64 / (1024.0 * 1024.0);

        _logger.LogInformation(
            "telemetry | conns_opened={ConnOpened} dropped={Dropped} | msgs out/s={OutRate:0} entities/s={EntRate:0} | snapshots/s={SnapRate:0} | tick ms mean={TickMean:0.0} max={TickMax:0.0} missed={Missed} | cpu={Cpu:0.0}% mem={Mem:0}MB",
            now.Counter(TelemetryMetrics.ConnectionsOpened),
            now.EventCount(TelemetryEvents.ConnectionDropped),
            Rate(TelemetryMetrics.MessagesOut),
            entitiesPerSecond,
            Rate(TelemetryMetrics.SnapshotsEmitted),
            tickMeanMs,
            tickNow.Max,
            now.Counter(TelemetryMetrics.MissedTicks),
            cpuPercent,
            memMb);

        _previous = now;
        _previousCpu = cpuNow;
        _previousWall = wallNow;
    }
}
