using GameServer.Observability;
using GameServer.Protocol;
using GameServer.Simulation;
using GameServer.Tenancy;
using GameServer.Transport;
using Xunit;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// Test #1 (two-tenant noisy neighbor). One tenant floods the realtime command path far past its
/// fair share; the OTHER tenant sends a polite, in-budget stream. With the per-tenant token bucket
/// now ENFORCED on the hot path (Wave 1), the noisy tenant is throttled and sheds, while the quiet
/// tenant's reject rate stays ZERO — its latency/admission is unaffected by its neighbor. This is
/// the in-process, deterministic proof that mirrors the over-the-wire <c>noisy-neighbor.json</c>
/// load scenario (run through the harness against a live host).
/// </summary>
/// <remarks>
/// Deterministic: the limiter reads a <see cref="FakeMonotonicClock"/> that never advances during
/// the burst, so no refill masks the shedding — no wall-clock, no real sleep.
/// </remarks>
public sealed class NoisyNeighborScenario
{
    private readonly ITestOutputHelper _output;

    public NoisyNeighborScenario(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task OneTenantFlood_DoesNotRaiseAnotherTenantsRejectRate()
    {
        const int noisyVolume = 500; // tenant-a slams the command path
        const int quietVolume = 20;  // tenant-b sends a modest, in-budget stream
        const int burst = 50;        // each tenant's independent allowance (clock frozen ⇒ no refill)

        var clock = new FrozenMonotonicClock();
        var limiter = new TokenBucketTenantRateLimiter(permitsPerSecond: 50, burst: burst, clock);
        var perTenant = new TenantScopedMetrics();

        var harness = new IntegrationHarness(
            _ => new MoveRightGame(),
            tenants: new[] { "tenant-a", "tenant-b" },
            rateLimiter: limiter,
            tenantMetrics: perTenant);

        // tenant-a floods; tenant-b trickles. Both run against the SAME limiter, so if buckets
        // leaked across tenants, tenant-b's polite stream would also start getting shed.
        var noisy = await harness.RunClientAsync("tenant-a", "arena-a", "p1", "demo", Repeat(MoveRightGame.MoveRight, noisyVolume));
        var quiet = await harness.RunClientAsync("tenant-b", "arena-b", "p1", "demo", Repeat(MoveRightGame.MoveRight, quietVolume));

        var noisyRejects = RateLimitRejects(noisy);
        var quietRejects = RateLimitRejects(quiet);

        _output.WriteLine($"tenant-a rate-limit rejects: {noisyRejects}/{noisyVolume}; tenant-b rejects: {quietRejects}/{quietVolume}");

        // The noisy tenant is throttled to its burst: everything past the allowance is shed.
        Assert.Equal(noisyVolume - burst, noisyRejects);
        // The quiet tenant, well within its own (separate) allowance, is NOT affected at all.
        Assert.Equal(0, quietRejects);

        // Observability: the per-tenant view names tenant-a as the driver (the global sink cannot).
        var ranked = perTenant.Ranked(TelemetryMetrics.MessagesIn, topN: 2);
        Assert.Equal("tenant-a", ranked[0].Tenant);
    }

    // Rate-limit shedding surfaces as ServerError(Overloaded) (→ BACKPRESSURE_REJECTED on the wire).
    private static int RateLimitRejects(InMemoryBidirectionalTransport transport) =>
        transport.DrainOutbound()
            .Select(m => m.Payload)
            .OfType<ServerError>()
            .Count(e => e.Code == ServerErrorCode.Overloaded);

    private static IReadOnlyList<string> Repeat(string command, int times) =>
        Enumerable.Repeat(command, times).ToArray();

    /// <summary>
    /// A monotonic clock frozen at zero: no time passes, so the token bucket never refills during
    /// the burst. This makes the shedding deterministic (exactly burst admitted, rest shed) with no
    /// wall-clock and no real sleep — the point of the injected-clock seam.
    /// </summary>
    private sealed class FrozenMonotonicClock : IMonotonicClock
    {
        public double ElapsedSeconds => 0d;
    }
}
