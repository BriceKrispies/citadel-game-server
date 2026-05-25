namespace GameServer.Tenancy;

/// <summary>
/// A deterministic <see cref="IMonotonicClock"/> whose time advances only when a test tells it to.
/// Lets rate-limiter and noisy-neighbor scenarios drive refill behavior with zero wall-clock and
/// zero real sleep, so the tests are fast and reproducible.
/// </summary>
public sealed class FakeMonotonicClock : IMonotonicClock
{
    private double _seconds;

    public double ElapsedSeconds => _seconds;

    /// <summary>Moves the clock forward. Never moves it backwards (a monotonic clock cannot).</summary>
    public void Advance(TimeSpan by)
    {
        if (by < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(by), by, "A monotonic clock cannot move backwards.");
        }

        _seconds += by.TotalSeconds;
    }
}
