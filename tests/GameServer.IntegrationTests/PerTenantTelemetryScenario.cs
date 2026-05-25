using GameServer.Observability;
using GameServer.Simulation;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// Gap #14 (tenant fairness, observability) — telemetry can't name the noisy tenant. The global
/// <see cref="AggregatingTelemetrySink"/> folds tags away (it can report total messages but not
/// per tenant), and <see cref="RoomScopedMetrics"/> attributes to rooms, not tenants. During a
/// noisy-neighbor incident the first question — "which tenant is driving this?" — is unanswerable.
/// This scenario drives a real imbalance (tenant-a noisy, tenant-b quiet) through the realtime edge
/// with a <see cref="TenantScopedMetrics"/> sink wired into the server, and asserts the per-tenant
/// view — populated ONLY by the edge attribution on the hot path — can rank tenants by inbound
/// volume so the noisy one surfaces first.
/// </summary>
public sealed class PerTenantTelemetryScenario
{
    private readonly ITestOutputHelper _output;

    public PerTenantTelemetryScenario(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task PerTenantView_NamesTheNoisyTenant_WhereTheGlobalViewCannot()
    {
        const int noisyVolume = 50;
        const int quietVolume = 2;

        // The per-tenant sink is wired INTO the server, so it is fed only by the edge attribution on
        // the hot path (RealtimeServer's receive loop). Nothing in this test records into it by hand —
        // every count must come from real traffic flowing through HandleConnectionAsync.
        var perTenant = new TenantScopedMetrics();
        var harness = new IntegrationHarness(
            _ => new MoveRightGame(),
            tenants: new[] { "tenant-a", "tenant-b" },
            tenantMetrics: perTenant);

        // Real traffic: tenant-a floods, tenant-b trickles. Each client also sends hello + join, so
        // the attributed inbound count is (commands + 2) per tenant — the exact edge-counted volume.
        await harness.RunClientAsync("tenant-a", "arena", "p1", "demo", Repeat(MoveRightGame.MoveRight, noisyVolume));
        await harness.RunClientAsync("tenant-b", "arena", "p1", "demo", Repeat(MoveRightGame.MoveRight, quietVolume));

        // The global sink saw the messages but cannot attribute them to a tenant (tags folded). The
        // per-tenant view must: rank tenants by inbound volume so tenant-a surfaces first.
        var ranked = perTenant.Ranked(TelemetryMetrics.MessagesIn, topN: 2);

        _output.WriteLine($"noisiest tenant by messages_in: {ranked.FirstOrDefault()?.Tenant ?? "<none>"}");

        Assert.Equal("tenant-a", ranked[0].Tenant);
        Assert.True(ranked[0].Total > ranked[1].Total);
        // Counts come ONLY from the edge (hello + join + N commands), proving the hot-path Record is live.
        Assert.Equal(noisyVolume + 2, ranked.Single(s => s.Tenant == "tenant-a").Total);
        Assert.Equal(quietVolume + 2, ranked.Single(s => s.Tenant == "tenant-b").Total);
    }

    private static IReadOnlyList<string> Repeat(string command, int times) =>
        Enumerable.Repeat(command, times).ToArray();
}
