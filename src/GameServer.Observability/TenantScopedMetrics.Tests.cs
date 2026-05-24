using Xunit;

namespace GameServer.Observability;

/// <summary>
/// Contract guards for the <see cref="TenantScopedMetrics"/> seam: construction validates the
/// cardinality cap, and record/rank report they are not implemented yet.
/// </summary>
public sealed class TenantScopedMetricsTests
{
    [Fact]
    public void Construction_WithNonPositiveCap_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TenantScopedMetrics(maxTenants: 0));
    }

    [Fact]
    public void Record_IsNotImplementedYet()
    {
        var metrics = new TenantScopedMetrics();
        Assert.Throws<NotImplementedException>(() => metrics.Record("tenant-a", TelemetryMetrics.MessagesIn, 1));
    }

    [Fact]
    public void Ranked_IsNotImplementedYet()
    {
        var metrics = new TenantScopedMetrics();
        Assert.Throws<NotImplementedException>(() => metrics.Ranked(TelemetryMetrics.MessagesIn, 3));
    }
}
