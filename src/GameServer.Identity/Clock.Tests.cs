using Xunit;

namespace GameServer.Identity;

public sealed class SystemClockTests
{
    [Fact]
    public void SystemClock_DelegatesToWallClock()
    {
        var clock = new SystemClock();

        var before = DateTimeOffset.UtcNow;
        var reading = clock.UtcNow;
        var after = DateTimeOffset.UtcNow;

        // The adapter must report the real wall clock (the one place it is allowed).
        Assert.True(reading >= before && reading <= after);
    }
}
