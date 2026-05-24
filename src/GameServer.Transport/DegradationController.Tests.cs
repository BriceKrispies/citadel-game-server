using Xunit;

namespace GameServer.Transport;

/// <summary>
/// Contract guards for the <see cref="LadderDegradationController"/> seam: construction validates
/// the tick budget, and observing health signals reports it is not implemented yet. The ladder
/// ordering is asserted here too, since callers depend on higher levels shedding more.
/// </summary>
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
    public void Observe_IsNotImplementedYet()
    {
        var controller = new LadderDegradationController(tickBudgetMs: 16.0);
        Assert.Throws<NotImplementedException>(() => controller.Observe(missedTickRate: 0.5, tickP95Ms: 40.0));
    }
}
