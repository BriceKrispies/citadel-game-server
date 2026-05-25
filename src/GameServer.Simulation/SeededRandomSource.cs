namespace GameServer.Simulation;

/// <summary>
/// Production random source seeded for reproducibility. Never uses
/// <c>Random.Shared</c> or any ambient/global nondeterminism, so a given seed
/// yields the same sequence — important for replay and debugging.
/// </summary>
public sealed class SeededRandomSource : IRandomSource
{
#pragma warning disable CA5394 // Deterministic seeding is intentional: this is simulation randomness, not security.
    private Random _random;

    public SeededRandomSource(int seed = 1)
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
