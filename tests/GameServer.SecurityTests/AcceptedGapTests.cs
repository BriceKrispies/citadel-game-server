namespace GameServer.SecurityTests;

/// <summary>
/// Tracked ACCEPTED GAPS — behaviors a future hardening pass should add, recorded here as
/// permanently-skipped tests so they are visible in every run (and in FINDINGS.md) without
/// failing the suite. A skip reason states the gap; flip to a real <c>[SkippableFact]</c> body
/// once the gap is closed.
/// </summary>
public sealed class AcceptedGapTests
{
    [SkippableFact]
    public void PerTenantRateLimiting_IsNotYetWiredToTheHotPath()
    {
        // GAP: TokenBucketTenantRateLimiter (GameServer.Tenancy) exists and is unit-tested, but is
        // NOT wired into RealtimeServer's command path. A single tenant can therefore push commands
        // up to the per-room queue depth (1024) on every room without a per-tenant token-bucket
        // ceiling throttling it first. Overload is currently shed by queue depth + admission caps,
        // not by fair per-tenant rate limiting. Black-box: there is no RATE_LIMITED behavior to
        // observe, so this stays skipped until the limiter is wired (then assert ServerError
        // RATE_LIMITED / ERROR_CODE_RATE_LIMITED under a sustained single-tenant flood).
        Skip.If(true,
            "Accepted gap: TokenBucketTenantRateLimiter is built + unit-tested but not wired to the " +
            "realtime hot path. No per-tenant RATE_LIMITED behavior is observable black-box yet.");
    }

    [SkippableFact]
    public void PerTenantComputeBudget_IsNotYetWiredToTheHotPath()
    {
        // GAP: FairTenantComputeBudget (GameServer.Tenancy) exists and is unit-tested, but is NOT
        // wired into the tick/replication path. There is no per-tenant CPU/compute fairness applied
        // under load, so one noisy tenant's rooms can consume more than a fair share of tick budget.
        // Nothing to observe black-box yet; revisit once the budget is wired into RoomTickService.
        Skip.If(true,
            "Accepted gap: FairTenantComputeBudget is built + unit-tested but not wired to the tick " +
            "path. No per-tenant compute-fairness behavior is observable black-box yet.");
    }
}
