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
        // Frozen clock: no time passes during the loop, so exactly the burst is admitted and every
        // request after it is throttled. Deterministic by construction — no wall-clock, no InRange
        // slop (refill is covered by Refill_IsDriven_ByTheInjectedClock_Deterministically).
        var clock = new FakeMonotonicClock();
        var limiter = new TokenBucketTenantRateLimiter(permitsPerSecond: 100, burst: 100, clock);
        var tenant = new TenantId("tenant-a");

        var admitted = 0;
        for (var i = 0; i < 1000; i++)
        {
            if (limiter.TryAcquire(tenant))
            {
                admitted++;
            }
        }

        // Exactly the burst is admitted; the very next request is throttled.
        Assert.Equal(100, admitted);
        Assert.False(limiter.TryAcquire(tenant));
    }

    [Fact]
    public void Refill_IsDriven_ByTheInjectedClock_Deterministically()
    {
        // With a fake clock that never advances, the bucket only ever holds its initial burst:
        // no time passes, so no tokens refill. This pins refill to the injected clock (no
        // wall-clock, no real sleep) and proves the limiter is deterministic under test.
        var clock = new FakeMonotonicClock();
        var limiter = new TokenBucketTenantRateLimiter(permitsPerSecond: 100, burst: 100, clock);
        var tenant = new TenantId("tenant-a");

        var admittedWithoutTime = 0;
        for (var i = 0; i < 1000; i++)
        {
            if (limiter.TryAcquire(tenant))
            {
                admittedWithoutTime++;
            }
        }

        Assert.Equal(100, admittedWithoutTime); // exactly the burst — clock frozen, zero refill

        // Advance exactly one second: the bucket refills by exactly permitsPerSecond.
        clock.Advance(TimeSpan.FromSeconds(1));
        var admittedAfterOneSecond = 0;
        for (var i = 0; i < 1000; i++)
        {
            if (limiter.TryAcquire(tenant))
            {
                admittedAfterOneSecond++;
            }
        }

        Assert.Equal(100, admittedAfterOneSecond); // exactly one second's worth of refill
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
