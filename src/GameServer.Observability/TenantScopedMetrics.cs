namespace GameServer.Observability;

/// <summary>Per-tenant aggregate of a metric (e.g. that tenant's inbound message count / tick cost).</summary>
public sealed record TenantStat(string Tenant, long Count, double Total, double Max);

/// <summary>
/// Bounded-cardinality per-tenant telemetry. The global <see cref="AggregatingTelemetrySink"/>
/// folds tags away (it can report total messages but not per tenant), and
/// <see cref="RoomScopedMetrics"/> attributes to rooms, not tenants. During a noisy-neighbor
/// incident the first question — "which tenant is driving this?" — is unanswerable. This keeps
/// per-tenant aggregates so an operator can rank tenants by message rate, tick cost, or
/// rejections, while bounding cardinality the same way rooms are bounded: at most
/// <c>maxTenants</c> tenants per metric, evicting the coldest (lowest total) when full so the
/// noisy ones are always retained.
/// </summary>
public sealed class TenantScopedMetrics : ITenantMetricsSink
{
    private readonly int _maxTenants;
    private readonly object _lock = new();
    private readonly Dictionary<string, Dictionary<string, Accumulator>> _byMetric = new();
    private long _evicted;

    public TenantScopedMetrics(int maxTenants = 4096)
    {
        if (maxTenants <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTenants), maxTenants, "Tracked-tenant cap must be positive.");
        }

        _maxTenants = maxTenants;
    }

    /// <summary>How many (tenant, metric) series have been evicted under cardinality pressure.</summary>
    public long EvictedTenants
    {
        get { lock (_lock) { return _evicted; } }
    }

    /// <summary>Records one observation of <paramref name="metric"/> for <paramref name="tenant"/>.</summary>
    public void Record(string tenant, string metric, double value)
    {
        lock (_lock)
        {
            if (!_byMetric.TryGetValue(metric, out var byTenant))
            {
                byTenant = new Dictionary<string, Accumulator>();
                _byMetric[metric] = byTenant;
            }

            if (!byTenant.TryGetValue(tenant, out var accumulator))
            {
                if (byTenant.Count >= _maxTenants)
                {
                    EvictColdest(byTenant);
                }

                accumulator = new Accumulator();
                byTenant[tenant] = accumulator;
            }

            accumulator.Add(value);
        }
    }

    /// <summary>The tenants ranked by total of <paramref name="metric"/>, descending — the noisy ones first.</summary>
    public IReadOnlyList<TenantStat> Ranked(string metric, int topN)
    {
        lock (_lock)
        {
            if (!_byMetric.TryGetValue(metric, out var byTenant))
            {
                return Array.Empty<TenantStat>();
            }

            return byTenant
                .Select(kv => new TenantStat(kv.Key, kv.Value.Count, kv.Value.Total, kv.Value.Max))
                .OrderByDescending(s => s.Total)
                .Take(topN)
                .ToList();
        }
    }

    private void EvictColdest(Dictionary<string, Accumulator> byTenant)
    {
        var coldest = byTenant.OrderBy(kv => kv.Value.Total).First().Key;
        byTenant.Remove(coldest);
        _evicted++;
    }

    private sealed class Accumulator
    {
        private long _count;
        private double _total;
        private double _max = double.NegativeInfinity;

        public long Count => _count;
        public double Total => _total;
        public double Max => _count > 0 ? _max : 0;

        public void Add(double value)
        {
            _count++;
            _total += value;
            if (value > _max)
            {
                _max = value;
            }
        }
    }
}
