using System.Diagnostics;

namespace GameServer.LoadHarness;

/// <summary>
/// Monotonic clock seam for latency measurement. Production uses a high-resolution
/// stopwatch; tests inject a fake so latency math is deterministic and free of
/// wall-clock time.
/// </summary>
public interface ILoadClock
{
    /// <summary>An opaque, monotonic timestamp.</summary>
    long GetTimestamp();

    /// <summary>Elapsed time since a timestamp returned by <see cref="GetTimestamp"/>.</summary>
    TimeSpan GetElapsed(long startTimestamp);
}

/// <summary>Production clock backed by <see cref="Stopwatch"/> (no wall-clock dependency).</summary>
public sealed class SystemLoadClock : ILoadClock
{
    public long GetTimestamp() => Stopwatch.GetTimestamp();

    public TimeSpan GetElapsed(long startTimestamp) => Stopwatch.GetElapsedTime(startTimestamp);
}
