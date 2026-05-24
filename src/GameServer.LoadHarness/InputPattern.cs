namespace GameServer.LoadHarness;

/// <summary>
/// Decides the cadence of <c>ClientInputFrame</c> sends for a virtual client. The
/// pattern is a pure function of the configured rate and the scenario kind, so the
/// real run can pace inputs with a timer while tests reason about it deterministically.
/// </summary>
public sealed class InputPattern
{
    private readonly double _ratePerSecond;
    private readonly bool _bursty;

    private InputPattern(double ratePerSecond, bool bursty)
    {
        _ratePerSecond = ratePerSecond;
        _bursty = bursty;
    }

    public static InputPattern FromConfig(ScenarioConfig config)
    {
        var bursty = string.Equals(config.ScenarioType, "burst-input", StringComparison.OrdinalIgnoreCase);
        return new InputPattern(config.InputRatePerClientPerSecond, bursty);
    }

    /// <summary>True when this pattern ever sends input (idle scenarios send none).</summary>
    public bool SendsInput => _ratePerSecond > 0;

    /// <summary>The steady interval between inputs for a sustained pattern.</summary>
    public TimeSpan SteadyInterval =>
        _ratePerSecond <= 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(1.0 / _ratePerSecond);

    /// <summary>
    /// The delay before input number <paramref name="sentCount"/> (zero-based). Burst
    /// patterns send a tight burst then idle; sustained patterns use a fixed interval.
    /// </summary>
    public TimeSpan DelayBefore(long sentCount)
    {
        if (!SendsInput)
        {
            return Timeout.InfiniteTimeSpan;
        }

        if (!_bursty)
        {
            return SteadyInterval;
        }

        // Burst: 10 inputs back-to-back, then a one-second lull.
        const int burstSize = 10;
        return sentCount % burstSize == 0 && sentCount > 0
            ? TimeSpan.FromSeconds(1)
            : TimeSpan.Zero;
    }
}
