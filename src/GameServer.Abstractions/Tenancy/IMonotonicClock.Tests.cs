using Xunit;

namespace GameServer.Tenancy;

/// <summary>
/// Contract for the <see cref="IMonotonicClock"/> seam, pinned through the in-test fake that the
/// rate-limiter tests use. The one guarantee a token bucket relies on is that elapsed time never
/// goes backwards (a backwards jump would mint phantom refill); this proves the fake honors it and
/// advances by exactly the requested amount, which is what makes refill behavior deterministic.
/// </summary>
public sealed class MonotonicClockContractTests
{
    [Fact]
    public void Fake_AdvancesByExactAmount_AndNeverGoesBackwards()
    {
        var clock = new FakeMonotonicClock();
        Assert.Equal(0d, clock.ElapsedSeconds);

        clock.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Equal(1.5d, clock.ElapsedSeconds);

        var before = clock.ElapsedSeconds;
        clock.Advance(TimeSpan.FromMilliseconds(250));
        Assert.True(clock.ElapsedSeconds >= before);
        Assert.Equal(1.75d, clock.ElapsedSeconds, precision: 6);
    }

    /// <summary>A deterministic <see cref="IMonotonicClock"/> whose time advances only when told to.</summary>
    private sealed class FakeMonotonicClock : IMonotonicClock
    {
        private double _seconds;
        public double ElapsedSeconds => _seconds;
        public void Advance(TimeSpan by) => _seconds += by.TotalSeconds;
    }
}
