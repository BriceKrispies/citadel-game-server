namespace GameServer.Simulation;

/// <summary>
/// Production logical clock: a deterministic tick counter with no wall-clock time.
/// Ticks advance only when something drives the simulation, so behavior stays
/// reproducible. <see cref="Reset"/> repositions the clock during recovery.
/// </summary>
public sealed class LogicalSimulationClock : ISimulationClock
{
    public LogicalSimulationClock(long startTick = 0) => CurrentTick = startTick;

    public long CurrentTick { get; private set; }

    public long Advance() => ++CurrentTick;

    public void Reset(long tick) => CurrentTick = tick;
}
