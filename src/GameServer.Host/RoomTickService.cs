using GameServer.Observability;
using GameServer.Routing;
using GameServer.Transport;

namespace GameServer.Host;

/// <summary>
/// Drives the authoritative simulation: on a fixed wall-clock cadence it ticks every
/// active room and fans out the resulting authoritative snapshots. Wall-clock time
/// lives here in the host, never in the simulation kernel. How rooms are scheduled
/// (serial vs concurrent) is delegated to an <see cref="IRoomTickScheduler"/> so the
/// authoritative loop can use all cores and one slow room cannot block the others. Each
/// cadence iteration is measured (tick_duration_ms) and over-budget iterations are
/// counted (missed_ticks), so saturation of the tick loop is observable.
/// </summary>
/// <remarks>
/// Runs as a supervised worker rather than a bare hosted service: a fault in the tick
/// loop is restarted (and counted) by the <see cref="WorkerSupervisor"/> instead of
/// stopping the whole host, and the supervisor cancels it on a bounded graceful shutdown.
/// </remarks>
public sealed class RoomTickService : ISupervisedWorker
{
    /// <summary>The default authoritative cadence when <c>Realtime:TickHz</c> is unset (10 Hz).</summary>
    public const double DefaultTickHz = 10.0;

    private readonly TimeSpan _tickInterval;
    private readonly RealtimeServer _server;
    private readonly IRoomTickScheduler _scheduler;
    private readonly ITelemetrySink _telemetry;
    private readonly RoomScopedMetrics _roomMetrics;
    private readonly ILogger<RoomTickService> _logger;
    // The observe→act loop for graceful degradation: each completed cycle's duration is recorded into a
    // trailing window, then the ladder is fed the windowed missed-tick rate / tick p95 so it escalates
    // under sustained overload and de-escalates as load subsides. The EDGE (RealtimeServer) reads the
    // resulting level to shed optional load. Required in the deployed host (wired at the composition
    // root); a unit harness may construct the service without it (then ticking does not degrade).
    private readonly IDegradationController _degradation;
    private readonly TickHealthWindow _health;

    public RoomTickService(
        RealtimeServer server,
        IRoomTickScheduler scheduler,
        ITelemetrySink telemetry,
        RoomScopedMetrics roomMetrics,
        ILogger<RoomTickService> logger,
        IDegradationController degradation,
        double tickHz = DefaultTickHz)
    {
        _server = server;
        _scheduler = scheduler;
        _telemetry = telemetry;
        _roomMetrics = roomMetrics;
        _logger = logger;
        _degradation = degradation;
        _tickInterval = IntervalForHz(tickHz);
        // The window's budget is the tick interval: a cycle longer than the interval is, by definition,
        // a missed tick. Sized so escalation/recovery react within a few seconds at the configured rate.
        _health = new TickHealthWindow(_tickInterval.TotalMilliseconds);
    }

    /// <summary>The wall-clock interval between authoritative tick cycles (1 / Hz).</summary>
    public TimeSpan TickInterval => _tickInterval;

    /// <summary>Converts a positive tick rate (Hz) to the cadence interval, guarding bad config.</summary>
    public static TimeSpan IntervalForHz(double tickHz)
    {
        if (tickHz <= 0 || double.IsNaN(tickHz) || double.IsInfinity(tickHz))
        {
            tickHz = DefaultTickHz;
        }

        return TimeSpan.FromMilliseconds(1000.0 / tickHz);
    }

    public string Name => "room-tick";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_tickInterval);

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var rooms = _server.ActiveRooms;
            if (rooms.Count == 0)
            {
                // No work this cycle: record a zero-cost cycle and feed the ladder so a server that has
                // drained back to idle recovers DOWN the degradation ladder instead of staying degraded.
                _health.Record(0);
                _degradation.Observe(_health.MissedTickRate, _health.TickP95Ms);
                continue;
            }

            var report = await _scheduler.TickCycleAsync(rooms, TickRoomResiliently, cancellationToken).ConfigureAwait(false);

            _telemetry.Measure(TelemetryMetrics.TickDurationMs, report.TotalElapsedMs);
            if (report.TotalElapsedMs > _tickInterval.TotalMilliseconds)
            {
                // The cadence iteration did not fit in the tick budget.
                _telemetry.Increment(TelemetryMetrics.MissedTicks);
            }

            // Close the observe→act loop: record this cycle's cost and feed the windowed health signals to
            // the degradation ladder. The ladder escalates immediately under overload and steps back down
            // as the window clears, and the edge reads the resulting level to shed/restore optional load.
            _health.Record(report.TotalElapsedMs);
            _degradation.Observe(_health.MissedTickRate, _health.TickP95Ms);

            // Per-room tick cost (bounded cardinality): lets ops answer "which room is hot?",
            // which the global by-name telemetry cannot.
            foreach (var sample in report.Samples)
            {
                _roomMetrics.Record($"{sample.Room.TenantId.Value}/{sample.Room.RoomId.Value}", sample.ElapsedMs);
            }
        }
    }

    // One room's failure must not abort the cycle (and so cancel sibling rooms). The
    // scheduler propagates exceptions, so resilience is applied here, per room.
    private async Task TickRoomResiliently(RoomKey room, CancellationToken cancellationToken)
    {
        try
        {
            await _server.TickRoom(room, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Tick failed for room {Room}", room);
        }
    }
}
