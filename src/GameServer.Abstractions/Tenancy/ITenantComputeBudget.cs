using GameServer.Protocol;

namespace GameServer.Tenancy;

/// <summary>
/// Per-tenant fair-share budgeting of simulation compute within a scheduling cycle. Rooms are
/// ticked from a shared worker pool with no notion of tenant, so a tenant running many expensive
/// rooms (heavy game logic) can monopolize the tick workers and delay every other tenant's ticks
/// — a CPU noisy-neighbor. A compute budget bounds how much tick time a tenant may consume per
/// cycle, so an over-budget tenant's rooms yield and a quiet tenant's cheap room still ticks on
/// cadence.
/// </summary>
/// <remarks>
/// STATUS: NOT WIRED to the tick path. This port and its <c>FairTenantComputeBudget</c> implementation
/// are built and unit-tested, but no composition root consults them when scheduling ticks (the tick
/// driver ticks every active room each cycle regardless of tenant cost). It is therefore a dormant seam,
/// not an active control — do not present per-tenant CPU fairness as a shipped capability. Wiring it
/// requires reconciling tenant fairness with the Wave-6 invariant that the authoritative simulation is
/// NEVER dropped: an "over budget" room must be DEFERRED within the cadence, never skipped. Tracked in
/// tickets/FINAL-GAP-REPORT.md (Gap B).
/// </remarks>
public interface ITenantComputeBudget
{
    /// <summary>
    /// Attempts to charge <paramref name="cost"/> of tick time to <paramref name="tenant"/> for
    /// the current cycle. Returns true if the tenant still has budget (tick it), false if the
    /// tenant has spent its fair share this cycle (defer it). A tenant exhausting its budget must
    /// never reduce another tenant's remaining budget.
    /// </summary>
    bool TryConsume(TenantId tenant, TimeSpan cost);

    /// <summary>Resets every tenant's per-cycle budget at the start of a new scheduling cycle.</summary>
    void BeginCycle();
}
