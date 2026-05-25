using Xunit;

namespace GameServer.Host;

public sealed class TickHealthWindowTests
{
    [Fact]
    public void Construction_WithNonPositiveBudget_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new TickHealthWindow(budgetMs: 0));

    [Fact]
    public void Construction_WithNonPositiveCapacity_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new TickHealthWindow(budgetMs: 100, capacity: 0));

    [Fact]
    public void Empty_ReportsZeroSignals()
    {
        var window = new TickHealthWindow(budgetMs: 100);
        Assert.Equal(0, window.MissedTickRate);
        Assert.Equal(0, window.TickP95Ms);
    }

    [Fact]
    public void AllUnderBudget_ReportsNoMissedTicks()
    {
        var window = new TickHealthWindow(budgetMs: 100);
        for (var i = 0; i < 10; i++)
        {
            window.Record(20);
        }

        Assert.Equal(0, window.MissedTickRate);
        Assert.Equal(20, window.TickP95Ms);
    }

    [Fact]
    public void AllOverBudget_ReportsFullMissRate()
    {
        var window = new TickHealthWindow(budgetMs: 100);
        for (var i = 0; i < 10; i++)
        {
            window.Record(300);
        }

        Assert.Equal(1.0, window.MissedTickRate);
        Assert.Equal(300, window.TickP95Ms);
    }

    [Fact]
    public void MixedCycles_ReportFractionalMissRate()
    {
        var window = new TickHealthWindow(budgetMs: 100);
        // 4 over budget, 6 under -> 0.4 miss rate.
        foreach (var cost in new double[] { 300, 300, 300, 300, 10, 10, 10, 10, 10, 10 })
        {
            window.Record(cost);
        }

        Assert.Equal(0.4, window.MissedTickRate, precision: 3);
    }

    [Fact]
    public void Window_ForgetsOldCycles_SoRecoveryIsVisible()
    {
        // A small window so a burst of overload ages out as healthy cycles arrive — this is what lets the
        // ladder climb back DOWN. A lifetime aggregate would keep the miss rate elevated forever.
        var window = new TickHealthWindow(budgetMs: 100, capacity: 4);
        for (var i = 0; i < 4; i++)
        {
            window.Record(500); // overload fills the window
        }

        Assert.Equal(1.0, window.MissedTickRate);

        for (var i = 0; i < 4; i++)
        {
            window.Record(10); // recovery: healthy cycles evict the overload samples
        }

        Assert.Equal(0, window.MissedTickRate);
        Assert.Equal(10, window.TickP95Ms);
    }

    [Fact]
    public void P95_IgnoresASingleOutlierWithinTheWindow()
    {
        var window = new TickHealthWindow(budgetMs: 100, capacity: 20);
        for (var i = 0; i < 19; i++)
        {
            window.Record(10);
        }

        window.Record(999); // one spike out of 20 stays below the 95th percentile
        Assert.Equal(10, window.TickP95Ms);
    }
}
