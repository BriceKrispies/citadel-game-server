using GameServer.Observability;

namespace GameServer.Transport;

/// <summary>
/// A long-running unit of work the host owns and the supervisor manages. A worker is
/// expected to run until its <paramref name="cancellationToken"/> is signalled
/// (cooperative shutdown). If it throws, the supervisor restarts it with backoff and
/// counts the restart; if it returns cleanly while still running, the supervisor treats
/// that as the worker choosing to stop and does not restart it.
/// </summary>
/// <remarks>
/// This is the in-process seam for independent, restartable workers. The same contract
/// can later back an out-of-process worker (one that proxies <see cref="RunAsync"/> to a
/// child process), so moving a worker across a process boundary for true crash
/// containment becomes an implementation swap rather than a redesign.
/// </remarks>
public interface ISupervisedWorker
{
    /// <summary>Stable, low-cardinality name used in restart telemetry and logs.</summary>
    string Name { get; }

    /// <summary>Runs the worker until it completes or <paramref name="cancellationToken"/> trips.</summary>
    Task RunAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Exponential backoff between worker restarts: a worker that fails repeatedly is retried
/// progressively less aggressively (up to a cap) so a persistently-poisoned worker cannot
/// hot-loop the process. The first restart waits <see cref="InitialBackoff"/>.
/// </summary>
public sealed record RestartPolicy(TimeSpan InitialBackoff, TimeSpan MaxBackoff, double Multiplier)
{
    public static RestartPolicy Default { get; } =
        new(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(10), 2.0);

    /// <summary>Backoff before the <paramref name="consecutiveFailures"/>-th restart (1-based).</summary>
    public TimeSpan BackoffFor(int consecutiveFailures)
    {
        if (consecutiveFailures <= 1)
        {
            return InitialBackoff;
        }

        var scaled = InitialBackoff.Ticks * Math.Pow(Multiplier, consecutiveFailures - 1);
        var capped = Math.Min(scaled, MaxBackoff.Ticks);
        return TimeSpan.FromTicks((long)capped);
    }
}

/// <summary>
/// Runs a fixed set of <see cref="ISupervisedWorker"/>s as independent, supervised units:
/// each runs in its own loop, a fault in one neither stops nor is observed by the others,
/// and a faulting worker is restarted with backoff (and counted as
/// <see cref="TelemetryMetrics.WorkerRestartCount"/>). On stop, the supervisor cancels
/// every worker and waits a bounded drain window; a worker that ignores cancellation is
/// abandoned (and reported) rather than allowed to hang the process.
/// </summary>
/// <remarks>
/// Deliberately framework-free: it depends only on the observability seam, not on ASP.NET
/// or the .NET hosting types, so it is exercised with fake workers and a fake delay (no
/// real time). A thin hosted-service adapter wires it into the host lifetime.
/// </remarks>
public sealed class WorkerSupervisor
{
    private readonly IReadOnlyList<ISupervisedWorker> _workers;
    private readonly ITelemetrySink _telemetry;
    private readonly RestartPolicy _policy;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _drainTimeout;

    public WorkerSupervisor(
        IEnumerable<ISupervisedWorker> workers,
        ITelemetrySink telemetry,
        RestartPolicy? policy = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? drainTimeout = null)
    {
        _workers = workers.ToArray();
        _telemetry = telemetry;
        _policy = policy ?? RestartPolicy.Default;
        // The delay seam is what keeps backoff and drain testable without real waiting.
        _delay = delay ?? Task.Delay;
        _drainTimeout = drainTimeout ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// Supervises every worker until <paramref name="cancellationToken"/> is signalled, then
    /// drains them within the configured budget. Returns once all workers have stopped or the
    /// drain window elapses. Never throws for a worker fault — faults are restarted, not raised.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var loops = _workers.Select(w => (Worker: w, Task: SuperviseAsync(w, cancellationToken))).ToArray();
        var all = Task.WhenAll(loops.Select(l => l.Task));

        // Once stop is requested, cooperative workers' loops return promptly and `all`
        // completes. A worker that ignores cancellation would keep its loop alive forever, so
        // race the drain against a bounded budget and abandon (report) any straggler.
        var winner = await Task.WhenAny(all, DrainBudgetAsync(cancellationToken)).ConfigureAwait(false);
        if (winner != all)
        {
            foreach (var (worker, task) in loops)
            {
                if (!task.IsCompleted)
                {
                    _telemetry.Event(TelemetryEvents.WorkerDrainTimedOut, Tags(("worker", worker.Name)));
                }
            }
        }
    }

    private async Task SuperviseAsync(ISupervisedWorker worker, CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await worker.RunAsync(cancellationToken).ConfigureAwait(false);

                // A clean return while still running means the worker chose to stop. That is
                // unexpected for a loop-until-cancel worker, so it is observable, but it is not
                // a fault: do not restart (a worker that always returns must not hot-loop).
                if (!cancellationToken.IsCancellationRequested)
                {
                    _telemetry.Event(TelemetryEvents.WorkerStopped, Tags(("worker", worker.Name)));
                }

                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return; // Graceful shutdown — not a fault.
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                _telemetry.Increment(TelemetryMetrics.WorkerRestartCount, Tags(("worker", worker.Name)));
                _telemetry.Event(TelemetryEvents.WorkerFaulted,
                    Tags(("worker", worker.Name), ("error", ex.GetType().Name), ("failures", consecutiveFailures.ToString())));

                try
                {
                    await _delay(_policy.BackoffFor(consecutiveFailures), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return; // Stop requested during backoff.
                }
            }
        }
    }

    private async Task DrainBudgetAsync(CancellationToken cancellationToken)
    {
        // Wait until stop is requested, then allow a bounded window for workers to drain.
        if (!cancellationToken.IsCancellationRequested)
        {
            var stopRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(static s => ((TaskCompletionSource)s!).TrySetResult(), stopRequested);
            await stopRequested.Task.ConfigureAwait(false);
        }

        // The drain window itself must not be cancellable by the stop token (that is the very
        // signal that started it), so it runs to completion on its own.
        await _delay(_drainTimeout, CancellationToken.None).ConfigureAwait(false);
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
