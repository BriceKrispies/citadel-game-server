namespace GameServer.ControlPlane;

/// <summary>
/// An authenticated control-plane caller: who they are (for audit), the tenant they
/// are scoped to, and any roles they hold. Authorization for tenant-scoped operations
/// flows through <see cref="CanActFor"/> — a caller may only act within its own tenant
/// unless it holds the platform-admin role.
/// </summary>
public sealed record CallerPrincipal(string CallerId, string TenantId, IReadOnlySet<string> Roles)
{
    public const string PlatformAdminRole = "platform-admin";

    /// <summary>True when this caller is allowed to act for <paramref name="tenantId"/>.</summary>
    public bool CanActFor(string tenantId) =>
        Roles.Contains(PlatformAdminRole) || string.Equals(TenantId, tenantId, StringComparison.Ordinal);
}
