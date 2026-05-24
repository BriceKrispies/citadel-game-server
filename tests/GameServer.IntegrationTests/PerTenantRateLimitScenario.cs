using GameServer.Protocol;
using GameServer.Tenancy;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// Gap #12 (tenant fairness) — no per-tenant ingress rate limit. The only inbound bound today is
/// the per-room command queue, which is per room: a tenant can flood the server by spreading
/// traffic across many of its own rooms, consuming global capacity and inflating every other
/// tenant's latency. The fix is a per-tenant token bucket on the receive path so a tenant's burst
/// is throttled to its fair share and shed cleanly, while other tenants are untouched. This
/// scenario drives a realistic burst (tenant-a hammering, tenant-b trickling) through the limiter
/// and asserts the fairness outcome. It FAILS today via the unimplemented
/// <see cref="ITenantRateLimiter"/> seam, and turns green once the token bucket exists and is
/// consulted before a command is enqueued.
/// </summary>
public sealed class PerTenantRateLimitScenario
{
    private readonly ITestOutputHelper _output;

    public PerTenantRateLimitScenario(ITestOutputHelper output) => _output = output;

    [Fact]
    public void OneTenantsCommandFlood_IsThrottled_WithoutAffectingAnotherTenant()
    {
        const int ratePerSecond = 100;
        const int burst = 100;
        const int floodSize = 1000; // tenant-a slams the server in a tight burst
        const int trickleSize = 10; // tenant-b sends a polite handful

        ITenantRateLimiter limiter = new TokenBucketTenantRateLimiter(ratePerSecond, burst);
        var noisy = new TenantId("tenant-a");
        var quiet = new TenantId("tenant-b");

        // A hardened limiter admits up to the tenant's burst+rate allowance and sheds the rest...
        var noisyAdmitted = CountAdmitted(limiter, noisy, floodSize);
        // ...while a different tenant's modest traffic is entirely unaffected by the flood.
        var quietAdmitted = CountAdmitted(limiter, quiet, trickleSize);

        _output.WriteLine($"tenant-a admitted {noisyAdmitted}/{floodSize}; tenant-b admitted {quietAdmitted}/{trickleSize}");

        Assert.InRange(noisyAdmitted, burst, burst + ratePerSecond); // throttled to its fair share
        Assert.Equal(trickleSize, quietAdmitted); // noisy neighbor did not consume tenant-b's allowance
    }

    private static int CountAdmitted(ITenantRateLimiter limiter, TenantId tenant, int attempts)
    {
        var admitted = 0;
        for (var i = 0; i < attempts; i++)
        {
            if (limiter.TryAcquire(tenant))
            {
                admitted++;
            }
        }

        return admitted;
    }
}
