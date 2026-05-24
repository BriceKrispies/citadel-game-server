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
