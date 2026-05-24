using GameServer.Observability;
using GameServer.Observability.Testing;
using Xunit;

namespace GameServer.Transport;

/// <summary>
/// Proves the supervision contract without depending on real time: backoff and drain go
/// through an injected delay that completes immediately and records what it was asked to
/// wait, and worker coordination uses gates/counters rather than sleeps. The supervisor
/// must restart faults (counted), isolate siblings, stop promptly on cancellation, and
/// never let a worker that ignores cancellation hang the drain.
/// </summary>
public sealed class WorkerSupervisorTests
{
    // A delay seam that never actually waits but records each requested duration, so backoff
    // is asserted on directly instead of being observed through wall-clock timing.
    private sealed class RecordingDelay
    {
        public List<TimeSpan> Requested { get; } = new();

        public Task Delay(TimeSpan duration, CancellationToken cancellationToken)
        {
            lock (Requested)
            {
                Requested.Add(duration);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class DelegateWorker : ISupervisedWorker
    {
        private readonly Func<CancellationToken, Task> _run;

        public DelegateWorker(string name, Func<CancellationToken, Task> run)
        {
            Name = name;
            _run = run;
        }

        public string Name { get; }

        public Task RunAsync(CancellationToken cancellationToken) => _run(cancellationToken);
    }

    [Fact]
    public async Task FaultingWorker_IsRestarted_AndCounted()
    {
        var telemetry = new TestTelemetrySink();
        var attempts = 0;
        var thirdAttemptReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var worker = new DelegateWorker("flaky", _ =>
        {
            var attempt = Interlocked.Increment(ref attempts);
            if (attempt < 3)
            {
                throw new InvalidOperationException("boom");
            }

            // Third run survives: signal, then block until the test cancels so it looks
            // like a healthy long-running worker.
            thirdAttemptReached.TrySetResult();
            return Task.Delay(Timeout.Infinite, cts.Token);
        });

        var supervisor = new WorkerSupervisor(new[] { worker }, telemetry, delay: (_, _) => Task.CompletedTask);
        var run = supervisor.RunAsync(cts.Token);

        await thirdAttemptReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Two faults -> two restarts, each counted; the worker is now running.
        Assert.Equal(2, telemetry.CountIncrements(TelemetryMetrics.WorkerRestartCount));
        Assert.True(telemetry.HasEvent(TelemetryEvents.WorkerFaulted));

        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Backoff_GrowsExponentially_UpToTheCap()
    {
        var telemetry = new TestTelemetrySink();
        var delay = new RecordingDelay();
        var policy = new RestartPolicy(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500), 2.0);
        using var cts = new CancellationTokenSource();

        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var worker = new DelegateWorker("always-fails", _ =>
        {
            if (Interlocked.Increment(ref attempts) >= 5)
            {
                settled.TrySetResult();
                return Task.Delay(Timeout.Infinite, cts.Token);
            }

            throw new InvalidOperationException("boom");
        });

        var supervisor = new WorkerSupervisor(new[] { worker }, telemetry, policy, delay.Delay);
        var run = supervisor.RunAsync(cts.Token);
        await settled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Four faults before the worker settles -> 100, 200, 400, then capped at 500.
        List<TimeSpan> requested;
        lock (delay.Requested)
        {
            requested = delay.Requested.Take(4).ToList();
        }

        Assert.Equal(
            new[] { 100.0, 200.0, 400.0, 500.0 },
            requested.Select(d => d.TotalMilliseconds).ToArray());

        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task OneWorkerFaulting_DoesNotStopSiblings()
    {
        var telemetry = new TestTelemetrySink();
        using var cts = new CancellationTokenSource();
        var healthyRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var healthy = new DelegateWorker("healthy", _ =>
        {
            healthyRunning.TrySetResult();
            return Task.Delay(Timeout.Infinite, cts.Token);
        });

        var faultCount = 0;
        var faulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var faulty = new DelegateWorker("faulty", _ =>
        {
            Interlocked.Increment(ref faultCount);
            faulted.TrySetResult();
            throw new InvalidOperationException("boom");
        });

        // Backoff parks the faulty worker after each fault (it does not return until cancelled),
        // so the restart loop cannot busy-spin — the fault is isolated, not amplified.
        var supervisor = new WorkerSupervisor(
            new[] { healthy, faulty }, telemetry, delay: (_, ct) => Task.Delay(Timeout.Infinite, ct));
        var run = supervisor.RunAsync(cts.Token);

        // The healthy worker is running and the faulty one has faulted (and been counted): the
        // fault is isolated to its own loop and has not stopped the supervisor or its sibling.
        await healthyRunning.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await faulted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(telemetry.CountIncrements(TelemetryMetrics.WorkerRestartCount) >= 1);
        Assert.False(run.IsCompleted);

        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Cancellation_StopsAllWorkers_Promptly()
    {
        var telemetry = new TestTelemetrySink();
        using var cts = new CancellationTokenSource();
        var started = 0;
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        ISupervisedWorker Cooperative(string name) => new DelegateWorker(name, async token =>
        {
            if (Interlocked.Increment(ref started) == 2)
            {
                bothStarted.TrySetResult();
            }

            await Task.Delay(Timeout.Infinite, token); // observes cancellation -> OperationCanceledException
        });

        // The drain budget has NOT elapsed (modelled by a delay that never completes), so
        // cooperative workers — which stop the instant they observe cancellation — must win
        // the drain race. A zero-duration fake budget would spuriously race their shutdown.
        var supervisor = new WorkerSupervisor(
            new[] { Cooperative("a"), Cooperative("b") }, telemetry,
            delay: (_, _) => new TaskCompletionSource().Task);
        var run = supervisor.RunAsync(cts.Token);

        await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await run.WaitAsync(TimeSpan.FromSeconds(5)); // completes without hanging
        Assert.False(telemetry.HasEvent(TelemetryEvents.WorkerDrainTimedOut));
    }

    [Fact]
    public async Task UncooperativeWorker_IsAbandoned_WithinDrainBudget()
    {
        var telemetry = new TestTelemetrySink();
        using var cts = new CancellationTokenSource();
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStuck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Ignores its cancellation token entirely; only the drain budget can free the supervisor.
        var stuck = new DelegateWorker("stuck", _ =>
        {
            running.TrySetResult();
            return releaseStuck.Task;
        });

        // Drain delay completes immediately (fake), so the budget elapses without real waiting.
        var supervisor = new WorkerSupervisor(
            new[] { stuck }, telemetry, delay: (_, _) => Task.CompletedTask, drainTimeout: TimeSpan.FromSeconds(30));
        var run = supervisor.RunAsync(cts.Token);

        await running.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await run.WaitAsync(TimeSpan.FromSeconds(5)); // does NOT wait for the stuck worker
        Assert.True(telemetry.HasEvent(TelemetryEvents.WorkerDrainTimedOut));

        releaseStuck.TrySetResult(); // let the abandoned worker finish so the test leaks nothing
    }

    [Fact]
    public void Backoff_FirstRestart_UsesInitial()
    {
        var policy = new RestartPolicy(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(10), 2.0);
        Assert.Equal(TimeSpan.FromMilliseconds(250), policy.BackoffFor(1));
        Assert.Equal(TimeSpan.FromMilliseconds(250), policy.BackoffFor(0));
    }
}
