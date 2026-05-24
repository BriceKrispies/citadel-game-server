using System.Diagnostics;
using GameServer.Observability;
using GameServer.Transport;

namespace GameServer.Host;

/// <summary>
/// Drives the authoritative simulation: on a fixed wall-clock cadence it ticks every
/// active room and fans out the resulting authoritative snapshots. Wall-clock time
/// lives here in the host, never in the simulation kernel. Each cadence iteration is
/// measured (tick_duration_ms) and over-budget iterations are counted (missed_ticks),
/// so saturation of the tick loop is observable.
/// </summary>
public sealed class RoomTickService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);

    private readonly RealtimeServer _server;
    private readonly ITelemetrySink _telemetry;
    private readonly ILogger<RoomTickService> _logger;

    public RoomTickService(RealtimeServer server, ITelemetrySink telemetry, ILogger<RoomTickService> logger)
    {
        _server = server;
        _telemetry = telemetry;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            var start = Stopwatch.GetTimestamp();

            foreach (var room in _server.ActiveRooms)
            {
                try
                {
                    await _server.TickRoom(room, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // One room's failure must not stop the global tick loop.
                    _logger.LogError(ex, "Tick failed for room {Room}", room);
                }
            }

            var elapsed = Stopwatch.GetElapsedTime(start);
            _telemetry.Measure(TelemetryMetrics.TickDurationMs, elapsed.TotalMilliseconds);
            if (elapsed > TickInterval)
            {
                // The fan-out for this iteration did not fit in the tick budget.
                _telemetry.Increment(TelemetryMetrics.MissedTicks);
            }
        }
    }
}
