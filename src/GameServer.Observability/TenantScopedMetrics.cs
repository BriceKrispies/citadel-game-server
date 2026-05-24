namespace GameServer.Observability;

/// <summary>Per-tenant aggregate of a metric (e.g. that tenant's inbound message count / tick cost).</summary>
public sealed record TenantStat(string Tenant, long Count, double Total, double Max);

/// <summary>
/// Bounded-cardinality per-tenant telemetry. The global <see cref="AggregatingTelemetrySink"/>
/// folds tags away, and <see cref="RoomScopedMetrics"/> attributes to rooms — neither can answer
/// "which TENANT is driving the load right now?", the first question during a noisy-neighbor
/// incident. This keeps per-tenant aggregates so an operator can rank tenants by message rate,
/// tick cost, or rejections, while bounding cardinality the same way rooms are bounded.
/// </summary>
/// <remarks>
/// RED-phase seam: the contract exists so per-tenant attribution can be pinned by a test
/// (<c>PerTenantTelemetryScenario</c>); recording and ranking are not implemented yet.
/// </remarks>
public sealed class TenantScopedMetrics
{
    private const string NotBuilt =
        "TenantScopedMetrics is a RED-phase seam: per-tenant telemetry attribution is not implemented yet.";

    public TenantScopedMetrics(int maxTenants = 4096)
    {
        if (maxTenants <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTenants), maxTenants, "Tracked-tenant cap must be positive.");
        }

        _maxTenants = maxTenants;
    }

    private readonly int _maxTenants;

    /// <summary>Records one observation of <paramref name="metric"/> for <paramref name="tenant"/>.</summary>
    public void Record(string tenant, string metric, double value) => throw new NotImplementedException(NotBuilt);

    /// <summary>The tenants ranked by total of <paramref name="metric"/>, descending — the noisy ones first.</summary>
    public IReadOnlyList<TenantStat> Ranked(string metric, int topN) => throw new NotImplementedException(NotBuilt);
}
