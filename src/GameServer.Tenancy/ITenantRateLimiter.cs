using GameServer.Protocol;

namespace GameServer.Tenancy;

/// <summary>
/// Per-tenant ingress rate limiting — the core noisy-neighbor control on the message path.
/// Today the only inbound bound is the per-room command queue, which is per room: one tenant
/// can still flood the server by spreading traffic across many of its own rooms, consuming
/// global CPU/IO and starving other tenants' latency. A rate limiter gives each tenant its own
/// token bucket so a tenant's burst is throttled to its fair share and shed cleanly, while other
/// tenants are unaffected.
/// </summary>
/// <remarks>
/// RED-phase seam: the contract exists so the fairness behavior can be pinned by a test
/// (<c>PerTenantRateLimitScenario</c>); the limiter and its placement on the receive path are
/// not built yet.
/// </remarks>
public interface ITenantRateLimiter
{
    /// <summary>
    /// Attempts to consume <paramref name="permits"/> from <paramref name="tenant"/>'s bucket.
    /// Returns true if within the tenant's allowance, false if the tenant is over its rate (the
    /// caller then sheds with <c>ServerError(Overloaded)</c>). One tenant being throttled must
    /// never affect another tenant's allowance.
    /// </summary>
    bool TryAcquire(TenantId tenant, int permits = 1);
}

/// <summary>Token-bucket rate limiter with an independent bucket per tenant.</summary>
public sealed class TokenBucketTenantRateLimiter : ITenantRateLimiter
{
    private const string NotBuilt =
        "TokenBucketTenantRateLimiter is a RED-phase seam: per-tenant rate limiting is not implemented yet.";

    /// <param name="permitsPerSecond">Sustained per-tenant refill rate.</param>
    /// <param name="burst">Maximum tokens a tenant may accumulate (burst allowance).</param>
    public TokenBucketTenantRateLimiter(int permitsPerSecond, int burst)
    {
        if (permitsPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(permitsPerSecond), permitsPerSecond, "Refill rate must be positive.");
        }

        if (burst <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(burst), burst, "Burst allowance must be positive.");
        }

        _permitsPerSecond = permitsPerSecond;
        _burst = burst;
    }

    private readonly int _permitsPerSecond;
    private readonly int _burst;

    public bool TryAcquire(TenantId tenant, int permits = 1) => throw new NotImplementedException(NotBuilt);
}
