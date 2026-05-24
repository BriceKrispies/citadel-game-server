using GameServer.Protocol;
using Xunit;

namespace GameServer.Tenancy;

public sealed class TenantComputeBudgetTests
{
    [Fact]
    public void Construction_WithNonPositiveBudget_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FairTenantComputeBudget(TimeSpan.Zero));
    }

    [Fact]
    public void Consumes_UpToBudget_ThenYields()
    {
        var budget = new FairTenantComputeBudget(TimeSpan.FromMilliseconds(20));
        var tenant = new TenantId("tenant-a");

        Assert.True(budget.TryConsume(tenant, TimeSpan.FromMilliseconds(15)));
        Assert.True(budget.TryConsume(tenant, TimeSpan.FromMilliseconds(5)));
        Assert.False(budget.TryConsume(tenant, TimeSpan.FromMilliseconds(1))); // budget exhausted
    }

    [Fact]
    public void OneTenantsSpend_DoesNotReduceAnotherTenantsBudget()
    {
        var budget = new FairTenantComputeBudget(TimeSpan.FromMilliseconds(20));

        // tenant-a blows through its whole slice...
        budget.TryConsume(new TenantId("tenant-a"), TimeSpan.FromMilliseconds(100));

        // ...tenant-b still has its own full slice.
        Assert.True(budget.TryConsume(new TenantId("tenant-b"), TimeSpan.FromMilliseconds(20)));
    }

    [Fact]
    public void BeginCycle_ResetsEveryTenantsBudget()
    {
        var budget = new FairTenantComputeBudget(TimeSpan.FromMilliseconds(20));
        var tenant = new TenantId("tenant-a");

        Assert.True(budget.TryConsume(tenant, TimeSpan.FromMilliseconds(20)));
        Assert.False(budget.TryConsume(tenant, TimeSpan.FromMilliseconds(1)));

        budget.BeginCycle();
        Assert.True(budget.TryConsume(tenant, TimeSpan.FromMilliseconds(20)));
    }
}
