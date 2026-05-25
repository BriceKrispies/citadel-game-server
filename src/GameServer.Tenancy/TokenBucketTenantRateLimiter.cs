using System.Collections.Concurrent;
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
/// <summary>
/// Token-bucket rate limiter with an independent bucket per tenant. Each tenant starts with a
/// full burst allowance and refills at <c>permitsPerSecond</c>; buckets are isolated, so one
/// tenant draining its bucket cannot consume another tenant's tokens.
/// </summary>
public sealed class TokenBucketTenantRateLimiter : ITenantRateLimiter
{
    private readonly int _permitsPerSecond;
    private readonly int _burst;
    private readonly IMonotonicClock _clock;
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();

    /// <param name="permitsPerSecond">Sustained per-tenant refill rate.</param>
    /// <param name="burst">Maximum tokens a tenant may accumulate (burst allowance).</param>
    /// <param name="clock">
    /// Monotonic time source for refill. Injected so refill is deterministic under test (a fake
    /// clock advances by an exact amount); defaults to the system high-resolution timer.
    /// </param>
    public TokenBucketTenantRateLimiter(int permitsPerSecond, int burst, IMonotonicClock? clock = null)
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
        _clock = clock ?? new SystemMonotonicClock();
    }

    public bool TryAcquire(TenantId tenant, int permits = 1)
    {
        if (permits <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(permits), permits, "Permits must be positive.");
        }

        var bucket = _buckets.GetOrAdd(tenant.Value, _ => new Bucket(_burst, NowSeconds()));
        lock (bucket)
        {
            var now = NowSeconds();
            var refill = (now - bucket.LastRefillSeconds) * _permitsPerSecond;
            bucket.Tokens = Math.Min(_burst, bucket.Tokens + refill);
            bucket.LastRefillSeconds = now;

            if (bucket.Tokens >= permits)
            {
                bucket.Tokens -= permits;
                return true;
            }

            return false;
        }
    }

    private double NowSeconds() => _clock.ElapsedSeconds;

    private sealed class Bucket
    {
        public Bucket(double tokens, double lastRefillSeconds)
        {
            Tokens = tokens;
            LastRefillSeconds = lastRefillSeconds;
        }

        public double Tokens { get; set; }
        public double LastRefillSeconds { get; set; }
    }
}
