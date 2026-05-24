using GameServer.Protocol;
using Xunit;

namespace GameServer.Tenancy;

/// <summary>
/// Contract guards for the <see cref="TokenBucketTenantRateLimiter"/> seam: construction validates
/// the rate/burst, and acquiring permits reports it is not implemented yet.
/// </summary>
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
    public void TryAcquire_IsNotImplementedYet()
    {
        var limiter = new TokenBucketTenantRateLimiter(permitsPerSecond: 100, burst: 100);
        Assert.Throws<NotImplementedException>(() => limiter.TryAcquire(new TenantId("tenant-a")));
    }
}
