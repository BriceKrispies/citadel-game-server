namespace GameServer.Observability;

/// <summary>
/// Edge-facing port for attributing a telemetry observation to a tenant. The global
/// <c>ITelemetrySink</c> folds tags away (it knows total messages, not per tenant), so during a
/// noisy-neighbor incident "which tenant is driving this?" is unanswerable from it alone. The
/// realtime edge records inbound volume here, per tenant, so an operator can rank tenants — while
/// the implementation bounds cardinality. Kept as a rank-0 port so the transport edge can feed it
/// without reaching into the Observability ring (the concrete aggregator is wired at the
/// composition root).
/// </summary>
public interface ITenantMetricsSink
{
    /// <summary>Records one observation of <paramref name="metric"/> for <paramref name="tenant"/>.</summary>
    void Record(string tenant, string metric, double value);
}
