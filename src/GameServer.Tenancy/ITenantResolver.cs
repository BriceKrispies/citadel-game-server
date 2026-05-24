using GameServer.Protocol;

namespace GameServer.Tenancy;

/// <summary>
/// Resolves a <see cref="TenantId"/> to its <see cref="TenantContext"/> at the
/// edge. Returns a result rather than throwing, because an unknown tenant is an
/// expected, observable rejection path — not an exceptional condition.
/// </summary>
public interface ITenantResolver
{
    bool TryResolve(TenantId tenantId, out TenantContext context);
}
