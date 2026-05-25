using Xunit;

namespace GameServer.Observability;

public sealed class TenantScopedMetricsTests
{
    [Fact]
    public void Construction_WithNonPositiveCap_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TenantScopedMetrics(maxTenants: 0));
    }

    [Fact]
    public void Ranked_NamesTheNoisiestTenant()
    {
        var metrics = new TenantScopedMetrics();
        metrics.Record("tenant-a", TelemetryMetrics.MessagesIn, 50);
        metrics.Record("tenant-b", TelemetryMetrics.MessagesIn, 2);

        var ranked = metrics.Ranked(TelemetryMetrics.MessagesIn, 2);

        Assert.Equal("tenant-a", ranked[0].Tenant);
        Assert.True(ranked[0].Total > ranked[1].Total);
    }

    [Fact]
    public void Record_AggregatesCountTotalAndMax_PerTenant()
    {
        var metrics = new TenantScopedMetrics();
        metrics.Record("t", TelemetryMetrics.MessagesIn, 10);
        metrics.Record("t", TelemetryMetrics.MessagesIn, 30);

        var stat = metrics.Ranked(TelemetryMetrics.MessagesIn, 1).Single();
        Assert.Equal(2, stat.Count);
        Assert.Equal(40, stat.Total);
        Assert.Equal(30, stat.Max);
    }

    [Fact]
    public void SatisfiesTheEdgePort_RecordingThroughITenantMetricsSink()
    {
        // The realtime edge feeds per-tenant volume through the rank-0 ITenantMetricsSink port;
        // the production aggregator must be substitutable for it (Liskov) so the wiring at the
        // composition root is honest.
        ITenantMetricsSink sink = new TenantScopedMetrics();
        sink.Record("tenant-a", TelemetryMetrics.MessagesIn, 7);

        var stat = ((TenantScopedMetrics)sink).Ranked(TelemetryMetrics.MessagesIn, 1).Single();
        Assert.Equal("tenant-a", stat.Tenant);
        Assert.Equal(7, stat.Total);
    }

    [Fact]
    public void Ranked_ForUnseenMetric_IsEmpty()
    {
        var metrics = new TenantScopedMetrics();
        Assert.Empty(metrics.Ranked("never_recorded", 3));
    }

    [Fact]
    public void Cardinality_IsBounded_EvictingTheColdestTenant()
    {
        var metrics = new TenantScopedMetrics(maxTenants: 2);
        metrics.Record("noisy", TelemetryMetrics.MessagesIn, 100);
        metrics.Record("warm", TelemetryMetrics.MessagesIn, 10);
        metrics.Record("cold", TelemetryMetrics.MessagesIn, 1); // evicts "warm" (lowest total)

        var tenants = metrics.Ranked(TelemetryMetrics.MessagesIn, 5).Select(s => s.Tenant).ToList();
        Assert.Equal(1, metrics.EvictedTenants);
        Assert.Contains("noisy", tenants);
        Assert.DoesNotContain("warm", tenants);
    }
}
