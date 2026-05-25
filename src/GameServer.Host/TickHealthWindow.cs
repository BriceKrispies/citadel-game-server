namespace GameServer.Host;

/// <summary>
/// A fixed-size sliding window of recent tick-cycle durations that derives the two health signals the
/// degradation ladder is driven by: the fraction of recent cycles that missed the tick budget
/// (<c>missed_ticks</c> rate) and the p95 cycle duration (<c>tick_duration_ms</c> p95). It exists so the
/// tick driver can turn its per-cycle measurements into a stable overload signal without reaching back
/// into the aggregating telemetry sink (which never forgets, so it cannot reflect RECOVERY).
/// </summary>
/// <remarks>
/// Single-writer: only the tick loop records into it, on its own cadence. Keeping a trailing window (not a
/// lifetime aggregate) is what lets the controller climb back DOWN the ladder once load subsides — a
/// lifetime mean would stay elevated forever after one overload. Deterministic and clock-free: it reasons
/// only over the durations it is handed, so a test can drive overload and recovery with injected numbers.
/// </remarks>
public sealed class TickHealthWindow
{
    private readonly double _budgetMs;
    private readonly double[] _samples;
    private int _count;
    private int _next;

    /// <param name="budgetMs">The per-cycle tick budget; a cycle longer than this is a missed tick.</param>
    /// <param name="capacity">How many recent cycles to retain (the window size).</param>
    public TickHealthWindow(double budgetMs, int capacity = 32)
    {
        if (budgetMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(budgetMs), budgetMs, "Tick budget must be positive.");
        }

        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Window capacity must be positive.");
        }

        _budgetMs = budgetMs;
        _samples = new double[capacity];
    }

    /// <summary>Records one completed tick-cycle's total duration into the window.</summary>
    public void Record(double cycleMs)
    {
        _samples[_next] = cycleMs;
        _next = (_next + 1) % _samples.Length;
        if (_count < _samples.Length)
        {
            _count++;
        }
    }

    /// <summary>The fraction of windowed cycles that exceeded the budget, in [0,1] (0 when empty).</summary>
    public double MissedTickRate
    {
        get
        {
            if (_count == 0)
            {
                return 0;
            }

            var missed = 0;
            for (var i = 0; i < _count; i++)
            {
                if (_samples[i] > _budgetMs)
                {
                    missed++;
                }
            }

            return missed / (double)_count;
        }
    }

    /// <summary>The p95 windowed cycle duration in ms (0 when empty).</summary>
    public double TickP95Ms
    {
        get
        {
            if (_count == 0)
            {
                return 0;
            }

            var sorted = new double[_count];
            Array.Copy(_samples, sorted, _count);
            Array.Sort(sorted);
            var rank = (int)Math.Ceiling(0.95 * _count) - 1;
            return sorted[Math.Clamp(rank, 0, _count - 1)];
        }
    }
}
