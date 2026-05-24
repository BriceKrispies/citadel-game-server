using GameServer.Protocol;

namespace GameServer.Tenancy;

/// <summary>
/// First-class tenant context resolved at the edge and carried explicitly through
/// the system. It is never ambient/global, and is never re-resolved inside hot
/// loops. Additional resolved tenant facts (db mapping, feature flags, quotas)
/// will hang off this type as the control plane grows.
/// </summary>
public sealed record TenantContext(TenantId TenantId, string DisplayName);
