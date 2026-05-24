using GameServer.Protocol;
using Xunit;

namespace GameServer.Tenancy;

/// <summary>
/// Contract guards for the <see cref="FairTenantComputeBudget"/> seam: construction validates the
/// cycle budget, and charging compute reports it is not implemented yet.
/// </summary>
public sealed class TenantComputeBudgetTests
{
    [Fact]
    public void Construction_WithNonPositiveBudget_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FairTenantComputeBudget(TimeSpan.Zero));
    }

    [Fact]
    public void TryConsume_IsNotImplementedYet()
    {
        var budget = new FairTenantComputeBudget(TimeSpan.FromMilliseconds(16));
        Assert.Throws<NotImplementedException>(() => budget.TryConsume(new TenantId("tenant-a"), TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public void BeginCycle_IsNotImplementedYet()
    {
        var budget = new FairTenantComputeBudget(TimeSpan.FromMilliseconds(16));
        Assert.Throws<NotImplementedException>(() => budget.BeginCycle());
    }
}
