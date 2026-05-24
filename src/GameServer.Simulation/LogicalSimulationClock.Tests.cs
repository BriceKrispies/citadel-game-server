using Xunit;

namespace GameServer.Simulation;

public sealed class LogicalSimulationClockTests
{
    [Fact]
    public void StartsAtZero_AndAdvancesByOne()
    {
        var clock = new LogicalSimulationClock();

        Assert.Equal(0L, clock.CurrentTick);
        Assert.Equal(1L, clock.Advance());
        Assert.Equal(1L, clock.CurrentTick);
    }

    [Fact]
    public void Reset_RepositionsTheClock()
    {
        var clock = new LogicalSimulationClock();
        clock.Advance();

        clock.Reset(42);

        Assert.Equal(42L, clock.CurrentTick);
        Assert.Equal(43L, clock.Advance());
    }

    [Fact]
    public void StartTick_SeedsInitialValue()
    {
        var clock = new LogicalSimulationClock(startTick: 10);

        Assert.Equal(10L, clock.CurrentTick);
    }
}
