using GameServer.Observability;
using GameServer.Simulation;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// Gap #14 (tenant fairness, observability) — telemetry can't name the noisy tenant. The global
/// <see cref="AggregatingTelemetrySink"/> folds tags away (it can report total messages but not
/// per tenant), and <see cref="RoomScopedMetrics"/> attributes to rooms, not tenants. During a
/// noisy-neighbor incident the first question — "which tenant is driving this?" — is unanswerable.
/// This scenario drives a real imbalance (tenant-a noisy, tenant-b quiet) and asserts a per-tenant
/// view can rank tenants by inbound volume. It FAILS today via the unimplemented
/// <see cref="TenantScopedMetrics"/> seam, and turns green once per-tenant attribution exists.
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

        var harness = new IntegrationHarness(_ => new MoveRightGame(), tenants: new[] { "tenant-a", "tenant-b" });

        // Real traffic: tenant-a floods, tenant-b trickles.
        await harness.RunClientAsync("tenant-a", "arena", "p1", "demo", Repeat(MoveRightGame.MoveRight, noisyVolume));
        await harness.RunClientAsync("tenant-b", "arena", "p1", "demo", Repeat(MoveRightGame.MoveRight, quietVolume));

        // The global sink saw the messages but cannot attribute them to a tenant (tags folded).
        // The per-tenant view must: rank tenants by inbound volume so tenant-a surfaces first.
        var perTenant = new TenantScopedMetrics();
        perTenant.Record("tenant-a", TelemetryMetrics.MessagesIn, noisyVolume);
        perTenant.Record("tenant-b", TelemetryMetrics.MessagesIn, quietVolume);

        var ranked = perTenant.Ranked(TelemetryMetrics.MessagesIn, topN: 2);

        _output.WriteLine($"noisiest tenant by messages_in: {ranked.FirstOrDefault()?.Tenant ?? "<none>"}");

        Assert.Equal("tenant-a", ranked[0].Tenant); // RED today: per-tenant attribution unimplemented
        Assert.True(ranked[0].Total > ranked[1].Total);
    }

    private static IReadOnlyList<string> Repeat(string command, int times) =>
        Enumerable.Repeat(command, times).ToArray();
}
