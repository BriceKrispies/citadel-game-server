namespace GameServer.Simulation;

/// <summary>
/// Logical, tick-based clock for the authoritative simulation. There is no
/// wall-clock time here on purpose: ticks advance only when the simulation is
/// driven, which makes every run deterministic and replayable. Production drivers
/// and test harnesses both advance it explicitly.
/// </summary>
public interface ISimulationClock
{
    /// <summary>The current tick. Starts at 0 before the first <see cref="Advance"/>.</summary>
    long CurrentTick { get; }

    /// <summary>Advances the simulation by one tick and returns the new tick number.</summary>
    long Advance();

    /// <summary>
    /// Positions the logical clock at a specific tick. Used by recovery to seed a
    /// restored room to its snapshot/replay tick. Valid because this is a logical
    /// (not wall-clock) clock; live play resumes by advancing from this point.
    /// </summary>
    void Reset(long tick);
}
