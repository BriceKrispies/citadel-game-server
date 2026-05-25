namespace GameServer.Simulation;

/// <summary>
/// Source of randomness for the simulation. Injected (never <c>Random.Shared</c>)
/// so a seeded, deterministic source can drive reproducible runs and replays.
/// Not used by the MoveRight slice yet, but established as a seam so future
/// stochastic behavior stays deterministic under test.
/// </summary>
public interface IRandomSource
{
    /// <summary>
    /// The seed this source was established with. Captured into a room's snapshot header so a
    /// fresh process can re-seed an identical source and replay deterministically.
    /// </summary>
    int Seed { get; }

    /// <summary>Returns a non-negative random integer less than <paramref name="maxExclusive"/>.</summary>
    int Next(int maxExclusive);

    /// <summary>Returns a random double in [0, 1).</summary>
    double NextDouble();

    /// <summary>
    /// Re-establishes the sequence from <paramref name="seed"/>, discarding any draws so far.
    /// A room restored from a snapshot calls this with the snapshot's captured seed so that
    /// post-snapshot replay reproduces the original room's stochastic decisions exactly.
    /// </summary>
    void Reseed(int seed);
}
