using GameServer.Observability;
using GameServer.Transport;

namespace GameServer.Host;

/// <summary>
/// Hosts the <see cref="WorkerSupervisor"/> inside the .NET host lifetime. It runs every
/// registered <see cref="ISupervisedWorker"/> under supervision — independent restart with
/// backoff, <see cref="TelemetryMetrics.WorkerRestartCount"/> — and ties the supervisor's
/// bounded graceful drain to the host's stopping signal.
/// </summary>
/// <remarks>
/// This is the only seam coupling worker orchestration to the hosting framework; the
/// supervisor itself is framework-free and unit-tested. The drain budget is set inside the
/// host's <c>ShutdownTimeout</c> so a wedged worker is reported and abandoned before the
/// host force-stops, rather than holding the process open.
/// </remarks>
public sealed class SupervisorHostedService : BackgroundService
{
    private readonly WorkerSupervisor _supervisor;

    public SupervisorHostedService(IEnumerable<ISupervisedWorker> workers, ITelemetrySink telemetry) =>
        _supervisor = new WorkerSupervisor(workers, telemetry, drainTimeout: TimeSpan.FromSeconds(3));

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => _supervisor.RunAsync(stoppingToken);
}
