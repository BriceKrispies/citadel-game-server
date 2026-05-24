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
/// RED-phase seam: the contract exists so the fair-scheduling behavior can be pinned by a test
/// (<c>PerTenantComputeBudgetScenario</c>); the budgeting and its integration into the room tick
/// scheduler are not built yet.
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

/// <summary>Equal-share-per-active-tenant compute budget for a scheduling cycle.</summary>
public sealed class FairTenantComputeBudget : ITenantComputeBudget
{
    private const string NotBuilt =
        "FairTenantComputeBudget is a RED-phase seam: per-tenant compute fair-sharing is not implemented yet.";

    /// <param name="cycleBudget">Total tick time available across all tenants in one cycle.</param>
    public FairTenantComputeBudget(TimeSpan cycleBudget)
    {
        if (cycleBudget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cycleBudget), cycleBudget, "Cycle budget must be positive.");
        }

        _cycleBudget = cycleBudget;
    }

    private readonly TimeSpan _cycleBudget;

    public bool TryConsume(TenantId tenant, TimeSpan cost) => throw new NotImplementedException(NotBuilt);

    public void BeginCycle() => throw new NotImplementedException(NotBuilt);
}
