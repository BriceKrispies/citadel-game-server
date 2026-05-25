using GameServer.Protocol;
using GameServer.Tenancy;

namespace GameServer.ControlPlane;

/// <summary>A tenant as the control plane stores it: its id and a human display name.</summary>
public sealed record TenantRecordDto(string TenantId, string DisplayName);

/// <summary>
/// The control-plane registry of tenants. Provisioning a tenant is a runtime API call against this
/// registry — NOT a code change — so adding a tenant never requires a redeploy. The realtime data plane
/// resolves tenants through the SAME registry instance (it also implements <see cref="ITenantResolver"/>),
/// so a newly-provisioned tenant is immediately resolvable at the edge.
/// </summary>
/// <remarks>
/// The read used on the hot path (<see cref="ITenantResolver.TryResolve"/>) reads an in-memory view, never
/// a database round-trip per command. A durable implementation write-throughs to its tenant catalog and
/// keeps that in-memory view warm.
/// </remarks>
public interface ITenantRegistry : ITenantResolver
{
    /// <summary>Creates a tenant. Returns false if one with that id already exists (idempotent-safe caller).</summary>
    bool TryCreate(TenantRecordDto tenant);

    /// <summary>Reads one tenant by id.</summary>
    bool TryGet(string tenantId, out TenantRecordDto tenant);

    /// <summary>Lists every registered tenant. Caller-side authorization decides what a principal may see.</summary>
    IReadOnlyList<TenantRecordDto> List();

    /// <summary>Removes a tenant. Returns false if it did not exist.</summary>
    bool TryDelete(string tenantId);
}

/// <summary>
/// In-memory tenant registry. Doubles as the realtime edge's <see cref="ITenantResolver"/>, so the same
/// instance that the control-plane CRUD endpoints mutate is the one the hot path reads — provisioning a
/// tenant via the API makes it resolvable with no code change and no redeploy. Honest substitute for the
/// durable (database-backed) registry: identical contract and the same unknown-tenant rejection.
/// </summary>
public sealed class InMemoryTenantRegistry : ITenantRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TenantRecordDto> _tenants = new(StringComparer.Ordinal);

    public InMemoryTenantRegistry()
    {
    }

    public InMemoryTenantRegistry(IEnumerable<TenantRecordDto> seed)
    {
        foreach (var tenant in seed)
        {
            _tenants[tenant.TenantId] = tenant;
        }
    }

    public bool TryCreate(TenantRecordDto tenant)
    {
        lock (_gate)
        {
            return _tenants.TryAdd(tenant.TenantId, tenant);
        }
    }

    public bool TryGet(string tenantId, out TenantRecordDto tenant)
    {
        lock (_gate)
        {
            return _tenants.TryGetValue(tenantId, out tenant!);
        }
    }

    public IReadOnlyList<TenantRecordDto> List()
    {
        lock (_gate)
        {
            return _tenants.Values.ToList();
        }
    }

    public bool TryDelete(string tenantId)
    {
        lock (_gate)
        {
            return _tenants.Remove(tenantId);
        }
    }

    // Hot-path read: in-memory dictionary lookup, never a database call.
    public bool TryResolve(TenantId tenantId, out TenantContext context)
    {
        lock (_gate)
        {
            if (_tenants.TryGetValue(tenantId.Value, out var record))
            {
                context = new TenantContext(tenantId, record.DisplayName);
                return true;
            }
        }

        context = null!;
        return false;
    }
}
