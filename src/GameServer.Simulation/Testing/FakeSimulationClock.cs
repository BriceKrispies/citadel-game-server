namespace GameServer.Simulation.Testing;

/// <summary>
/// Deterministic, manually-driven clock. Starts at tick 0 and only moves when the
/// simulation is ticked — no wall-clock time is ever read.
/// </summary>
/// <remarks>
/// Cross-feature test support: used by Simulation's own room tests and by the
/// transport-edge slice harness. Lives in <c>Testing/</c> so it compiles into the
/// test assembly only, never into production.
/// </remarks>
public sealed class FakeSimulationClock : ISimulationClock
{
    public long CurrentTick { get; private set; }

    public long Advance() => ++CurrentTick;

    public void Reset(long tick) => CurrentTick = tick;
}
