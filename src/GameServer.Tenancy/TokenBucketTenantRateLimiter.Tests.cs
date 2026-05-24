using GameServer.Protocol;
using Xunit;

namespace GameServer.Tenancy;

public sealed class TenantRateLimiterTests
{
    [Fact]
    public void Construction_WithNonPositiveRate_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBucketTenantRateLimiter(permitsPerSecond: 0, burst: 10));
    }

    [Fact]
    public void Construction_WithNonPositiveBurst_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBucketTenantRateLimiter(permitsPerSecond: 10, burst: 0));
    }

    [Fact]
    public void Admits_UpToBurst_ThenThrottles()
    {
        var limiter = new TokenBucketTenantRateLimiter(permitsPerSecond: 100, burst: 100);
        var tenant = new TenantId("tenant-a");

        var admitted = 0;
        for (var i = 0; i < 1000; i++)
        {
            if (limiter.TryAcquire(tenant))
            {
                admitted++;
            }
        }

        // Burst is consumed; only a trickle of refill is admitted over the (sub-ms) loop.
        Assert.InRange(admitted, 100, 200);
    }

    [Fact]
    public void Buckets_AreIsolatedPerTenant()
    {
        var limiter = new TokenBucketTenantRateLimiter(permitsPerSecond: 100, burst: 100);
        var noisy = new TenantId("tenant-a");
        var quiet = new TenantId("tenant-b");

        // Drain the noisy tenant's bucket entirely.
        for (var i = 0; i < 1000; i++)
        {
            limiter.TryAcquire(noisy);
        }

        // A different tenant still has its full allowance.
        Assert.True(limiter.TryAcquire(quiet));
    }
}
