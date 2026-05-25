namespace GameServer.Tenancy;

/// <summary>
/// A monotonic elapsed-time source for rate/budget accounting. Distinct from the wall-clock
/// <c>GameServer.Identity.IClock</c> (token expiry) and the logical
/// <c>GameServer.Simulation.ISimulationClock</c> (tick advancement): a token bucket needs a
/// steadily-increasing real-time reading that never goes backwards, but it must read it through a
/// seam — never <see cref="System.Diagnostics.Stopwatch"/> directly — so refill behavior is
/// deterministic under test (a fake can advance time by an exact amount).
/// </summary>
public interface IMonotonicClock
{
    /// <summary>Seconds elapsed since an arbitrary fixed origin. Strictly non-decreasing.</summary>
    double ElapsedSeconds { get; }
}
