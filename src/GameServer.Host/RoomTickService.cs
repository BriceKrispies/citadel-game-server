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
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);

    private readonly RealtimeServer _server;
    private readonly IRoomTickScheduler _scheduler;
    private readonly ITelemetrySink _telemetry;
    private readonly RoomScopedMetrics _roomMetrics;
    private readonly ILogger<RoomTickService> _logger;

    public RoomTickService(
        RealtimeServer server,
        IRoomTickScheduler scheduler,
        ITelemetrySink telemetry,
        RoomScopedMetrics roomMetrics,
        ILogger<RoomTickService> logger)
    {
        _server = server;
        _scheduler = scheduler;
        _telemetry = telemetry;
        _roomMetrics = roomMetrics;
        _logger = logger;
    }

    public string Name => "room-tick";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TickInterval);

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var rooms = _server.ActiveRooms;
            if (rooms.Count == 0)
            {
                continue;
            }

            var report = await _scheduler.TickCycleAsync(rooms, TickRoomResiliently, cancellationToken).ConfigureAwait(false);

            _telemetry.Measure(TelemetryMetrics.TickDurationMs, report.TotalElapsedMs);
            if (report.TotalElapsedMs > TickInterval.TotalMilliseconds)
            {
                // The cadence iteration did not fit in the tick budget.
                _telemetry.Increment(TelemetryMetrics.MissedTicks);
            }

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
