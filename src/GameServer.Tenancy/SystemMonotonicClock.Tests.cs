using Xunit;

namespace GameServer.Tenancy;

public sealed class SystemMonotonicClockTests
{
    [Fact]
    public void ElapsedSeconds_IsNonDecreasing_AcrossReads()
    {
        var clock = new SystemMonotonicClock();
        var first = clock.ElapsedSeconds;
        var second = clock.ElapsedSeconds;

        // The whole point of a monotonic clock: a later read is never earlier than an earlier one.
        Assert.True(second >= first, $"monotonic clock went backwards: {second} < {first}");
    }

    [Fact]
    public void ElapsedSeconds_Advances_OverRealWork()
    {
        var clock = new SystemMonotonicClock();
        var start = clock.ElapsedSeconds;

        // Burn a little wall time without sleeping the test thread on a fake clock seam.
        var spin = System.Diagnostics.Stopwatch.StartNew();
        while (spin.Elapsed < TimeSpan.FromMilliseconds(5))
        {
        }

        Assert.True(clock.ElapsedSeconds > start);
    }
}
