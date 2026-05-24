namespace GameServer.Transport;

/// <summary>
/// Ordered service-degradation levels. Higher levels shed more aggressively. The ladder is
/// ordered so correctness-critical work (authoritative simulation) is preserved to the last:
/// optional load is shed before gameplay, and new load is refused before existing load is hurt.
/// </summary>
public enum DegradationLevel
{
    /// <summary>Full service.</summary>
    Normal = 0,

    /// <summary>Send spectator/observer snapshots less often to reclaim fan-out budget.</summary>
    ReduceSpectatorSnapshots = 1,

    /// <summary>Drop non-critical telemetry payloads to reclaim CPU.</summary>
    ShedTelemetry = 2,

    /// <summary>Refuse new room creation (existing rooms keep ticking).</summary>
    RejectNewRooms = 3,

    /// <summary>Refuse new connections (last resort before correctness is at risk).</summary>
    RejectNewConnections = 4,
}

/// <summary>
/// Turns runtime health signals into an explicit, ordered degradation decision so the server
/// degrades gracefully instead of failing at a cliff. Today the server measures
/// <c>missed_ticks</c> and <c>tick_duration_ms</c> but nothing acts on them: admission is binary
/// (admit until a hard ceiling, then hard-reject), and there is no mechanism to trade optional
/// work for tick headroom under sustained overload. This controller closes that observe→act loop:
/// it consumes the health signals and escalates/de-escalates a <see cref="DegradationLevel"/> with
/// hysteresis.
/// </summary>
public interface IDegradationController
{
    /// <summary>The current degradation level (what the data plane should currently shed).</summary>
    DegradationLevel Current { get; }

    /// <summary>
    /// Feeds the latest tick-health window (fraction of ticks missed in [0,1], and the p95 tick
    /// duration) and returns the resulting level. Escalates as health worsens and recovers as it
    /// improves, with hysteresis so it does not flap.
    /// </summary>
    DegradationLevel Observe(double missedTickRate, double tickP95Ms);
}

/// <summary>
/// Threshold-laddered degradation controller driven by missed-tick rate and tick p95 (measured
/// against the tick budget). Escalation is immediate so the server reacts to overload at once;
/// recovery de-escalates one level at a time so it does not flap between levels on noisy signals.
/// </summary>
public sealed class LadderDegradationController : IDegradationController
{
    private readonly double _tickBudgetMs;
    private readonly object _lock = new();
    private DegradationLevel _current = DegradationLevel.Normal;

    /// <param name="tickBudgetMs">The per-tick budget; p95 beyond this is the overload signal.</param>
    public LadderDegradationController(double tickBudgetMs)
    {
        if (tickBudgetMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tickBudgetMs), tickBudgetMs, "Tick budget must be positive.");
        }

        _tickBudgetMs = tickBudgetMs;
    }

    public DegradationLevel Current
    {
        get { lock (_lock) { return _current; } }
    }

    public DegradationLevel Observe(double missedTickRate, double tickP95Ms)
    {
        var target = TargetFor(missedTickRate, tickP95Ms);
        lock (_lock)
        {
            if (target > _current)
            {
                // Escalate straight to the demanded level — overload needs an immediate response.
                _current = target;
            }
            else if (target < _current)
            {
                // Recover gently: step down one level at a time to avoid flapping.
                _current = (DegradationLevel)((int)_current - 1);
            }

            return _current;
        }
    }

    /// <summary>The level the current signals demand, before hysteresis is applied.</summary>
    private DegradationLevel TargetFor(double missedTickRate, double tickP95Ms)
    {
        // Approaching the budget (or missing the occasional tick) -> start shedding the cheapest,
        // most optional work first; blowing well past it -> climb toward refusing new load.
        if (missedTickRate > 0.75 || tickP95Ms > _tickBudgetMs * 2.0)
        {
            return DegradationLevel.RejectNewConnections;
        }

        if (missedTickRate > 0.5 || tickP95Ms > _tickBudgetMs * 1.5)
        {
            return DegradationLevel.RejectNewRooms;
        }

        if (missedTickRate > 0.25 || tickP95Ms > _tickBudgetMs)
        {
            return DegradationLevel.ShedTelemetry;
        }

        if (missedTickRate > 0.05 || tickP95Ms > _tickBudgetMs * 0.75)
        {
            return DegradationLevel.ReduceSpectatorSnapshots;
        }

        return DegradationLevel.Normal;
    }
}
