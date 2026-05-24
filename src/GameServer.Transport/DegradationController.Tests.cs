using Xunit;

namespace GameServer.Transport;

public sealed class DegradationControllerTests
{
    [Fact]
    public void Construction_WithNonPositiveBudget_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LadderDegradationController(tickBudgetMs: 0));
    }

    [Fact]
    public void Levels_AreOrdered_SoHigherShedsMore()
    {
        Assert.True(DegradationLevel.Normal < DegradationLevel.ReduceSpectatorSnapshots);
        Assert.True(DegradationLevel.ReduceSpectatorSnapshots < DegradationLevel.ShedTelemetry);
        Assert.True(DegradationLevel.ShedTelemetry < DegradationLevel.RejectNewRooms);
        Assert.True(DegradationLevel.RejectNewRooms < DegradationLevel.RejectNewConnections);
    }

    [Fact]
    public void HealthySignals_StayAtNormal()
    {
        var controller = new LadderDegradationController(tickBudgetMs: 50);
        Assert.Equal(DegradationLevel.Normal, controller.Observe(missedTickRate: 0.0, tickP95Ms: 10));
    }

    [Fact]
    public void Overload_EscalatesAboveNormal()
    {
        var controller = new LadderDegradationController(tickBudgetMs: 50);
        // p95 past the budget with missed ticks => shed beyond Normal.
        Assert.True(controller.Observe(missedTickRate: 0.5, tickP95Ms: 70) > DegradationLevel.Normal);
    }

    [Fact]
    public void WorseSignals_EscalateFurther()
    {
        var controller = new LadderDegradationController(tickBudgetMs: 50);
        var mild = controller.Observe(missedTickRate: 0.0, tickP95Ms: 40);   // approaching budget
        var severe = controller.Observe(missedTickRate: 0.9, tickP95Ms: 200); // far past budget
        Assert.True(severe > mild);
        Assert.Equal(DegradationLevel.RejectNewConnections, severe);
    }

    [Fact]
    public void Recovery_DeEscalatesGradually_NotInOneStep()
    {
        var controller = new LadderDegradationController(tickBudgetMs: 50);
        controller.Observe(missedTickRate: 0.9, tickP95Ms: 200); // -> RejectNewConnections (top)

        // Healthy again, but recovery steps down one level at a time (hysteresis).
        var afterFirstRecovery = controller.Observe(missedTickRate: 0.0, tickP95Ms: 5);
        Assert.Equal(DegradationLevel.RejectNewRooms, afterFirstRecovery);
        Assert.True(afterFirstRecovery < DegradationLevel.RejectNewConnections);
    }
}
