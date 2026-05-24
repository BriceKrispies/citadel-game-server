using GameServer.Observability;

namespace GameServer.Transport;

/// <summary>
/// A handle to a running child process the supervisor can monitor and stop. The seam that
/// keeps <see cref="OutOfProcessWorker"/> testable without spawning real OS processes.
/// </summary>
public interface IChildProcessHandle
{
    /// <summary>OS process id captured at launch (for telemetry/logs); 0 if unavailable.</summary>
    int Id { get; }

    /// <summary>Completes with the process exit code once it exits, or cancels with the token.</summary>
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>Requests a cooperative stop; a well-behaved child observes it and exits.</summary>
    void RequestStop();

    /// <summary>Forcibly terminates the child (and its tree). Best-effort, idempotent.</summary>
    void Kill();
}

/// <summary>Launches a child worker process. Abstracted so the worker can be tested with a fake.</summary>
public interface IChildProcessLauncher
{
    IChildProcessHandle Launch();
}

/// <summary>Raised when a child worker process exits abnormally, so the supervisor restarts it.</summary>
public sealed class OutOfProcessWorkerException : Exception
{
    public OutOfProcessWorkerException(string worker, int exitCode)
        : base($"Worker process '{worker}' exited with code {exitCode}.") => ExitCode = exitCode;

    public int ExitCode { get; }
}

/// <summary>
/// A supervised worker whose work runs in a SEPARATE OS process, so a process-fatal failure
/// — native access violation, OOM, stack overflow — is contained to the child: the parent
/// observes the non-zero exit, surfaces it as a fault, and the <see cref="WorkerSupervisor"/>
/// restarts a fresh child (counted as <see cref="TelemetryMetrics.WorkerRestartCount"/>). This
/// is the crash-containment counterpart to an in-process <see cref="ISupervisedWorker"/>: same
/// contract, same supervision, but a real isolation boundary an in-process loop cannot provide.
/// </summary>
/// <remarks>
/// On graceful shutdown the worker never orphans its child: it asks the child to stop, waits a
/// bounded grace, then force-kills and confirms exit. Wiring a specific payload (e.g. a room's
/// tick loop) into the child is a separate concern — it needs an IPC channel between parent and
/// child — and layers on top of this lifecycle without changing it.
/// </remarks>
public sealed class OutOfProcessWorker : ISupervisedWorker
{
    private readonly IChildProcessLauncher _launcher;
    private readonly ITelemetrySink _telemetry;
    private readonly TimeSpan _stopGrace;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public OutOfProcessWorker(
        string name,
        IChildProcessLauncher launcher,
        ITelemetrySink telemetry,
        TimeSpan? stopGrace = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        Name = name;
        _launcher = launcher;
        _telemetry = telemetry;
        _stopGrace = stopGrace ?? TimeSpan.FromSeconds(3);
        // Injected so the stop grace is exercised without real waiting in tests.
        _delay = delay ?? Task.Delay;
    }

    public string Name { get; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var child = _launcher.Launch();
        _telemetry.Event(TelemetryEvents.WorkerProcessStarted, Tags(("worker", Name), ("pid", child.Id.ToString())));

        try
        {
            var exitCode = await child.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (exitCode != 0)
            {
                // Abnormal exit: surface as a fault so the supervisor restarts + counts it.
                _telemetry.Event(TelemetryEvents.WorkerProcessCrashed, Tags(("worker", Name), ("exitCode", exitCode.ToString())));
                throw new OutOfProcessWorkerException(Name, exitCode);
            }

            // Exit 0 on its own: the child chose to stop. Not a fault — the supervisor stops here.
            _telemetry.Event(TelemetryEvents.WorkerProcessExited, Tags(("worker", Name), ("exitCode", "0")));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Graceful shutdown: never leave an orphan holding resources (the very failure mode
            // that motivates a bounded, supervised lifecycle).
            await StopChildAsync(child).ConfigureAwait(false);
        }
    }

    private async Task StopChildAsync(IChildProcessHandle child)
    {
        child.RequestStop();

        var exit = child.WaitForExitAsync(CancellationToken.None);
        var settled = await Task.WhenAny(exit, _delay(_stopGrace, CancellationToken.None)).ConfigureAwait(false);
        if (settled != exit)
        {
            // The child ignored the cooperative stop within the grace window: force it, then
            // confirm it is actually gone before returning.
            _telemetry.Event(TelemetryEvents.WorkerProcessKilled, Tags(("worker", Name), ("pid", child.Id.ToString())));
            child.Kill();
            await exit.ConfigureAwait(false);
        }
    }

    private static IReadOnlyDictionary<string, string> Tags(params (string Key, string Value)[] pairs)
    {
        var tags = new Dictionary<string, string>(pairs.Length);
        foreach (var (key, value) in pairs)
        {
            tags[key] = value;
        }

        return tags;
    }
}
