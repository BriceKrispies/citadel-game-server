using GameServer.Protocol;

namespace GameServer.Tenancy;

/// <summary>
/// In-memory tenant registry seeded with a known set of tenants. Honest
/// substitute for a future database/control-plane-backed resolver: same contract,
/// same rejection behavior for unknown tenants.
/// </summary>
public sealed class InMemoryTenantResolver : ITenantResolver
{
    private readonly Dictionary<TenantId, TenantContext> _tenants = new();

    public InMemoryTenantResolver(IEnumerable<TenantContext> tenants)
    {
        foreach (var tenant in tenants)
        {
            _tenants[tenant.TenantId] = tenant;
        }
    }

    /// <summary>Registers or replaces a tenant. Returns the resolver for fluent seeding.</summary>
    public InMemoryTenantResolver Add(TenantContext tenant)
    {
        _tenants[tenant.TenantId] = tenant;
        return this;
    }

    public bool TryResolve(TenantId tenantId, out TenantContext context) =>
        _tenants.TryGetValue(tenantId, out context!);
}
