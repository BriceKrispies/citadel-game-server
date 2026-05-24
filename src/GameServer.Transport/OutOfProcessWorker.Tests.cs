using GameServer.Observability;
using GameServer.Observability.Testing;
using Xunit;

namespace GameServer.Transport;

/// <summary>
/// Proves the out-of-process worker lifecycle without spawning real processes (a fake handle
/// drives exit/crash/stop deterministically) and without real time (the stop-grace delay is
/// the injected seam). Covers crash→fault, clean exit, cooperative stop, force-kill of a
/// child that ignores the stop, and — with the real supervisor — restart of a crashed child.
/// </summary>
public sealed class OutOfProcessWorkerTests
{
    private sealed class FakeChild : IChildProcessHandle
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Id => 4242;
        public int StopRequests;
        public int Kills;

        /// <summary>If set, the child cooperates: a stop request makes it exit with this code.</summary>
        public int? ExitCodeOnStop { get; init; }

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken) => _exit.Task.WaitAsync(cancellationToken);

        public void RequestStop()
        {
            Interlocked.Increment(ref StopRequests);
            if (ExitCodeOnStop is { } code)
            {
                _exit.TrySetResult(code);
            }
        }

        public void Kill()
        {
            Interlocked.Increment(ref Kills);
            _exit.TrySetResult(137); // 128 + SIGKILL, as a forced-termination exit code
        }

        public void Exit(int code) => _exit.TrySetResult(code);
    }

    private sealed class StubLauncher : IChildProcessLauncher
    {
        private readonly Func<IChildProcessHandle> _launch;
        public StubLauncher(Func<IChildProcessHandle> launch) => _launch = launch;
        public IChildProcessHandle Launch() => _launch();
    }

    [Fact]
    public async Task AbnormalExit_SurfacesAsFault()
    {
        var telemetry = new TestTelemetrySink();
        var child = new FakeChild();
        var worker = new OutOfProcessWorker("room-shard", new StubLauncher(() => child), telemetry);

        var run = worker.RunAsync(CancellationToken.None);
        child.Exit(139); // segfault-style exit

        var ex = await Assert.ThrowsAsync<OutOfProcessWorkerException>(() => run);
        Assert.Equal(139, ex.ExitCode);
        Assert.True(telemetry.HasEvent(TelemetryEvents.WorkerProcessCrashed));
    }

    [Fact]
    public async Task CleanExit_DoesNotFault()
    {
        var telemetry = new TestTelemetrySink();
        var child = new FakeChild();
        var worker = new OutOfProcessWorker("room-shard", new StubLauncher(() => child), telemetry);

        var run = worker.RunAsync(CancellationToken.None);
        child.Exit(0);

        await run.WaitAsync(TimeSpan.FromSeconds(5)); // completes without throwing
        Assert.True(telemetry.HasEvent(TelemetryEvents.WorkerProcessExited));
        Assert.False(telemetry.HasEvent(TelemetryEvents.WorkerProcessCrashed));
    }

    [Fact]
    public async Task Cancellation_StopsCooperativeChild_WithoutKilling()
    {
        var telemetry = new TestTelemetrySink();
        var child = new FakeChild { ExitCodeOnStop = 0 };
        // Grace never elapses (delay never completes): the child must exit via the stop request.
        var worker = new OutOfProcessWorker(
            "room-shard", new StubLauncher(() => child), telemetry, delay: (_, _) => new TaskCompletionSource().Task);

        using var cts = new CancellationTokenSource();
        var run = worker.RunAsync(cts.Token);
        cts.Cancel();

        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, child.StopRequests);
        Assert.Equal(0, child.Kills);
        Assert.False(telemetry.HasEvent(TelemetryEvents.WorkerProcessKilled));
    }

    [Fact]
    public async Task Cancellation_KillsChild_ThatIgnoresStop()
    {
        var telemetry = new TestTelemetrySink();
        var child = new FakeChild(); // does not exit on stop
        // Grace elapses immediately (fake delay), forcing the kill path.
        var worker = new OutOfProcessWorker(
            "room-shard", new StubLauncher(() => child), telemetry, delay: (_, _) => Task.CompletedTask);

        using var cts = new CancellationTokenSource();
        var run = worker.RunAsync(cts.Token);
        cts.Cancel();

        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, child.StopRequests);
        Assert.Equal(1, child.Kills);
        Assert.True(telemetry.HasEvent(TelemetryEvents.WorkerProcessKilled));
    }

    [Fact]
    public async Task Supervisor_RestartsCrashedChild_WhileParentSurvives()
    {
        var telemetry = new TestTelemetrySink();
        var launches = 0;
        var secondChildRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeChild? second = null;

        var launcher = new StubLauncher(() =>
        {
            var child = new FakeChild();
            if (Interlocked.Increment(ref launches) == 1)
            {
                child.Exit(139); // first child crashes immediately
            }
            else
            {
                second = child; // second child stays running until cancelled
                secondChildRunning.TrySetResult();
            }

            return child;
        });

        var worker = new OutOfProcessWorker("room-shard", launcher, telemetry, delay: (_, _) => Task.CompletedTask);
        // Immediate backoff is safe: only the first child crashes, so there is no hot-loop.
        var supervisor = new WorkerSupervisor(new[] { (ISupervisedWorker)worker }, telemetry, delay: (_, _) => Task.CompletedTask);

        using var cts = new CancellationTokenSource();
        var run = supervisor.RunAsync(cts.Token);

        // The crash was contained and a fresh child was started: supervision restarted it.
        await secondChildRunning.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, launches);
        Assert.Equal(1, telemetry.CountIncrements(TelemetryMetrics.WorkerRestartCount));
        Assert.False(run.IsCompleted); // the supervisor (parent) is unaffected by the child crash

        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
