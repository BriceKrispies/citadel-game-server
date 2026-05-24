using System.Collections.Concurrent;
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
/// <summary>
/// Gives every tenant the same independent compute slice per cycle. A tenant's spend is tracked
/// in isolation, so one tenant burning through its slice cannot eat into another tenant's — that
/// isolation is exactly what stops a CPU noisy-neighbor.
/// </summary>
public sealed class FairTenantComputeBudget : ITenantComputeBudget
{
    private readonly TimeSpan _perTenantBudget;
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, TimeSpan> _spent = new();

    /// <param name="perTenantBudget">The compute slice each tenant gets per scheduling cycle.</param>
    public FairTenantComputeBudget(TimeSpan perTenantBudget)
    {
        if (perTenantBudget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(perTenantBudget), perTenantBudget, "Per-tenant budget must be positive.");
        }

        _perTenantBudget = perTenantBudget;
    }

    public bool TryConsume(TenantId tenant, TimeSpan cost)
    {
        lock (_lock)
        {
            _spent.TryGetValue(tenant.Value, out var spent);
            if (spent + cost <= _perTenantBudget)
            {
                _spent[tenant.Value] = spent + cost;
                return true;
            }

            return false;
        }
    }

    public void BeginCycle()
    {
        lock (_lock)
        {
            _spent.Clear();
        }
    }
}
