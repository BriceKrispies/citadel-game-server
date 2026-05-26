namespace GameServer.Simulation.Testing;

/// <summary>
/// Seeded random source for tests. Reproducible across runs given the same seed, and never uses
/// <c>Random.Shared</c> or any ambient nondeterminism. Delegates to the production
/// <see cref="SeededRandomSource"/> so the test double behaves IDENTICALLY to the real source
/// (same sequence, same capture/restore semantics) — an honest substitute, never a divergent fake.
/// Cross-feature test support, so it lives in <c>Testing/</c>.
/// </summary>
public sealed class DeterministicRandomSource : IRandomSource
{
    private readonly SeededRandomSource _inner;

    public DeterministicRandomSource(int seed = 1) => _inner = new SeededRandomSource(seed);

    public int Seed => _inner.Seed;

    public int Next(int maxExclusive) => _inner.Next(maxExclusive);

    public double NextDouble() => _inner.NextDouble();

    public void Reseed(int seed) => _inner.Reseed(seed);

    public long CaptureState() => _inner.CaptureState();

    public void RestoreState(long state) => _inner.RestoreState(state);
}
