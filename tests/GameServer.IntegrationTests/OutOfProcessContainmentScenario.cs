using System.Diagnostics;
using GameServer.Observability;
using GameServer.Transport;

namespace GameServer.IntegrationTests;

/// <summary>
/// End-to-end proof of crash containment with REAL OS processes: a child worker that exits
/// abnormally is observed as a fault by <see cref="OutOfProcessWorker"/> and restarted by the
/// <see cref="WorkerSupervisor"/>, while this parent process keeps running (it is, after all,
/// the one making the assertions). This is the isolation guarantee an in-process supervised
/// worker cannot give. Uses real processes and real wall-clock time — an integration scenario,
/// kept out of the deterministic fast loop.
/// </summary>
public sealed class OutOfProcessContainmentScenario
{
    [Fact]
    public async Task RealCrashingChild_IsRestarted_WhileParentSurvives()
    {
        var telemetry = new AggregatingTelemetrySink();
        var launcher = new SystemProcessLauncher(CrashImmediately);
        var worker = new OutOfProcessWorker("crashy-shard", launcher, telemetry, stopGrace: TimeSpan.FromSeconds(1));

        // Small but real backoff so repeated crashes are throttled rather than spun.
        var policy = new RestartPolicy(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200), 2.0);
        var supervisor = new WorkerSupervisor(new[] { (ISupervisedWorker)worker }, telemetry, policy);

        using var cts = new CancellationTokenSource();
        var run = supervisor.RunAsync(cts.Token);

        // Wait until the supervisor has restarted the crashing child at least twice: each restart
        // means a real process crashed and was contained without taking down this process.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (telemetry.Snapshot().Counter(TelemetryMetrics.WorkerRestartCount) < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        var final = telemetry.Snapshot();
        Assert.True(
            final.Counter(TelemetryMetrics.WorkerRestartCount) >= 2,
            "expected the supervisor to restart the crashing external worker at least twice");
        Assert.True(final.EventCount(TelemetryEvents.WorkerProcessCrashed) >= 1);
    }

    // A child process that exits abnormally (non-zero) the instant it starts.
    private static ProcessStartInfo CrashImmediately() =>
        OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c exit 1") { CreateNoWindow = true, UseShellExecute = false }
            : new ProcessStartInfo("/bin/sh", "-c \"exit 1\"") { CreateNoWindow = true, UseShellExecute = false };
}
