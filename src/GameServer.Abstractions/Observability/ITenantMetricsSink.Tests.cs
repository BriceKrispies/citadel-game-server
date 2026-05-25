using Xunit;

namespace GameServer.Observability;

/// <summary>
/// Contract for the <see cref="ITenantMetricsSink"/> edge port: an observation is attributed to the
/// named tenant and metric. Pinned through a minimal fake; the production aggregator
/// (<c>TenantScopedMetrics</c>) is verified against the port in its own co-located tests.
/// </summary>
public sealed class TenantMetricsSinkContractTests
{
    [Fact]
    public void Record_AttributesValue_ToTenantAndMetric()
    {
        ITenantMetricsSink sink = new CapturingSink();
        sink.Record("tenant-a", "messages_in", 3);
        sink.Record("tenant-a", "messages_in", 2);
        sink.Record("tenant-b", "messages_in", 1);

        var capturing = (CapturingSink)sink;
        Assert.Equal(5, capturing.TotalFor("tenant-a", "messages_in"));
        Assert.Equal(1, capturing.TotalFor("tenant-b", "messages_in"));
    }

    private sealed class CapturingSink : ITenantMetricsSink
    {
        private readonly Dictionary<(string Tenant, string Metric), double> _totals = new();

        public void Record(string tenant, string metric, double value)
        {
            _totals.TryGetValue((tenant, metric), out var existing);
            _totals[(tenant, metric)] = existing + value;
        }

        public double TotalFor(string tenant, string metric) =>
            _totals.TryGetValue((tenant, metric), out var v) ? v : 0;
    }
}
