using System.Diagnostics;

namespace GameServer.Tenancy;

/// <summary>
/// The production <see cref="IMonotonicClock"/>: a steadily-increasing reading from the
/// process-wide high-resolution timer. This is the ONE place the rate/budget path is allowed to
/// read real time (<see cref="Stopwatch"/>) — every other component takes the seam, so it can be
/// faked deterministically in tests.
/// </summary>
public sealed class SystemMonotonicClock : IMonotonicClock
{
    public double ElapsedSeconds => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}
