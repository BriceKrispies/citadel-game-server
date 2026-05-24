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
/// <c>missed_ticks</c> and <c>tick_duration_ms</c> but nothing acts on them: admission is
/// binary (admit until a hard ceiling, then hard-reject), and there is no mechanism to trade
/// optional work for tick headroom under sustained overload. This controller closes that
/// observe→act loop: it consumes the health signals and escalates/de-escalates a
/// <see cref="DegradationLevel"/> with hysteresis.
/// </summary>
/// <remarks>
/// RED-phase seam: the contract exists so the degradation behavior can be pinned by a test
/// (<c>DegradationLadderScenario</c>); the laddering logic and its wiring into the tick driver
/// and admission path are not built yet.
/// </remarks>
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

/// <summary>Threshold-laddered degradation controller driven by missed-tick rate and tick p95.</summary>
public sealed class LadderDegradationController : IDegradationController
{
    private const string NotBuilt =
        "LadderDegradationController is a RED-phase seam: the degradation ladder is not implemented yet.";

    /// <param name="tickBudgetMs">The per-tick budget; p95 beyond this is the overload signal.</param>
    public LadderDegradationController(double tickBudgetMs)
    {
        if (tickBudgetMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tickBudgetMs), tickBudgetMs, "Tick budget must be positive.");
        }

        _tickBudgetMs = tickBudgetMs;
    }

    private readonly double _tickBudgetMs;

    public DegradationLevel Current => throw new NotImplementedException(NotBuilt);

    public DegradationLevel Observe(double missedTickRate, double tickP95Ms) =>
        throw new NotImplementedException(NotBuilt);
}
