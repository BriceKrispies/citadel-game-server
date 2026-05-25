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

    /// <summary>
    /// True when this caller holds the platform-admin role. Platform-scoped operations that act on fleet
    /// topology rather than one tenant's data (e.g. draining a node) gate on this, not on
    /// <see cref="CanActFor"/> (which is tenant-scoped).
    /// </summary>
    public bool IsPlatformAdmin => Roles.Contains(PlatformAdminRole);

    /// <summary>True when this caller is allowed to act for <paramref name="tenantId"/>.</summary>
    public bool CanActFor(string tenantId) =>
        IsPlatformAdmin || string.Equals(TenantId, tenantId, StringComparison.Ordinal);
}
