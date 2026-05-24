namespace GameServer.Simulation;

/// <summary>
/// Source of randomness for the simulation. Injected (never <c>Random.Shared</c>)
/// so a seeded, deterministic source can drive reproducible runs and replays.
/// Not used by the MoveRight slice yet, but established as a seam so future
/// stochastic behavior stays deterministic under test.
/// </summary>
public interface IRandomSource
{
    /// <summary>Returns a non-negative random integer less than <paramref name="maxExclusive"/>.</summary>
    int Next(int maxExclusive);

    /// <summary>Returns a random double in [0, 1).</summary>
    double NextDouble();
}
