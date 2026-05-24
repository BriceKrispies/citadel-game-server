namespace GameServer.Identity;

/// <summary>
/// A wall-clock source. Token issuance and expiry are real-time concerns (distinct
/// from the simulation's logical <c>ISimulationClock</c>), so they read time through
/// this seam — never <see cref="DateTimeOffset.UtcNow"/> directly — which keeps
/// expiry behavior deterministic in tests.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
