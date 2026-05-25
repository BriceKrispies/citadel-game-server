namespace GameServer.Simulation.Testing;

/// <summary>
/// Seeded random source. Reproducible across runs given the same seed, and never
/// uses <c>Random.Shared</c> or any ambient nondeterminism. Cross-feature test
/// support, so it lives in <c>Testing/</c>.
/// </summary>
public sealed class DeterministicRandomSource : IRandomSource
{
#pragma warning disable CA5394 // Deterministic seeding is the point: this is test/simulation randomness, not security.
    private Random _random;

    public DeterministicRandomSource(int seed = 1)
    {
        Seed = seed;
        _random = new Random(seed);
    }

    public int Seed { get; private set; }

    public int Next(int maxExclusive) => _random.Next(maxExclusive);

    public double NextDouble() => _random.NextDouble();

    public void Reseed(int seed)
    {
        Seed = seed;
        _random = new Random(seed);
    }
#pragma warning restore CA5394
}
