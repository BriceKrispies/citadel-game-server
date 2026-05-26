namespace GameServer.Simulation;

/// <summary>
/// Production random source seeded for reproducibility. Never uses <c>Random.Shared</c> or any
/// ambient/global nondeterminism, so a given seed yields the same sequence — important for replay
/// and debugging. Built on SplitMix64, whose entire state is a single 64-bit accumulator, so the
/// source can be captured and restored to an EXACT mid-sequence point (see
/// <see cref="CaptureState"/>) — the foundation for replaying a stochastic game to any past tick,
/// not just the genesis checkpoint.
/// </summary>
public sealed class SeededRandomSource : IRandomSource
{
    // SplitMix64 constants. The state advances by the golden-ratio gamma each draw, so capturing
    // and restoring the single accumulator reproduces the sequence exactly from that point.
    private const ulong Gamma = 0x9E3779B97F4A7C15UL;

    private ulong _state;

    public SeededRandomSource(int seed = 1)
    {
        Seed = seed;
        _state = InitialState(seed);
    }

    public int Seed { get; private set; }

    public int Next(int maxExclusive)
    {
        if (maxExclusive <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), maxExclusive, "Bound must be positive.");
        }

        // Scale a [0,1) double by the bound: stays strictly below maxExclusive, and consumes exactly
        // one draw so the state advances one step per call (keeping capture/restore aligned).
        return (int)(NextDouble() * maxExclusive);
    }

    public double NextDouble() => (NextRaw() >> 11) * (1.0 / (1UL << 53));

    public void Reseed(int seed)
    {
        Seed = seed;
        _state = InitialState(seed);
    }

    public long CaptureState() => unchecked((long)_state);

    public void RestoreState(long state) => _state = unchecked((ulong)state);

    private ulong NextRaw()
    {
        unchecked
        {
            _state += Gamma;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    private static ulong InitialState(int seed) => unchecked((ulong)(uint)seed);
}
