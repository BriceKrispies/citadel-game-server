using System.Diagnostics;
using GameServer.Observability;
using GameServer.Transport;

namespace GameServer.Host;

/// <summary>
/// Periodically flushes the aggregated telemetry as a compact rolling summary:
/// per-interval message/snapshot rates, tick-duration mean/max, missed ticks, and
/// process CPU/memory. The hot path only accumulates counters; this loop is the only
/// thing that reads and reports, so telemetry never amplifies the realtime load.
/// </summary>
/// <remarks>
/// Runs as a supervised worker (see <see cref="WorkerSupervisor"/>): a fault is restarted
/// and counted rather than taking down the host, and it is cancelled on graceful shutdown.
/// </remarks>
public sealed class TelemetryFlushService : ISupervisedWorker
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

    public string Name => "telemetry-flush";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _previousCpu = _process.TotalProcessorTime;
        _previousWall = Stopwatch.GetTimestamp();
        using var timer = new PeriodicTimer(FlushInterval);

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
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

        // Telemetry-derived rates come from the shared interval computation (the same one
        // the live /sim/telemetry feed uses); only process-level CPU/memory is local here.
        var rates = TelemetryRates.Between(_previous, now, seconds);

        var cpuNow = _process.TotalProcessorTime;
        var cpuPercent = (cpuNow - _previousCpu).TotalSeconds / (seconds * Environment.ProcessorCount) * 100.0;
        var memMb = _process.WorkingSet64 / (1024.0 * 1024.0);

        _logger.LogInformation(
            "telemetry | conns_opened={ConnOpened} dropped={Dropped} | msgs out/s={OutRate:0} entities/s={EntRate:0} | snapshots/s={SnapRate:0} | tick ms mean={TickMean:0.0} max={TickMax:0.0} missed={Missed} | cpu={Cpu:0.0}% mem={Mem:0}MB",
            rates.ConnectionsOpened,
            rates.ConnectionsDropped,
            rates.MessagesOutPerSecond,
            rates.EntitiesPerSecond,
            rates.SnapshotsPerSecond,
            rates.TickMeanMs,
            rates.TickMaxMs,
            rates.MissedTicks,
            cpuPercent,
            memMb);

        _previous = now;
        _previousCpu = cpuNow;
        _previousWall = wallNow;
    }
}
