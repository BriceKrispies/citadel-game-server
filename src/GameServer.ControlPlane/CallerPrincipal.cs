namespace GameServer.ControlPlane;

/// <summary>
/// An authenticated control-plane caller: who they are (for audit), the tenant they
/// are scoped to, and any roles they hold. Authorization for tenant-scoped operations
/// flows through <see cref="CanActFor"/> — a caller may only act within its own tenant
/// unless it holds the platform-admin role.
/// </summary>
/// <remarks>
/// Roles form a least-privilege ladder, enforced per endpoint (never "any authenticated caller may
/// mutate"):
/// <list type="bullet">
/// <item><b>operator</b> (read-only): may READ resources it is tenant-authorized for, but mutates nothing.</item>
/// <item><b>game-admin</b>: may mutate tenant-scoped resources (games, versions, rooms, limits) for the
/// tenants it can act for — but cannot perform platform-level operations (provisioning tenants, draining
/// nodes), which span the fleet.</item>
/// <item><b>platform-admin</b>: full authority across all tenants AND platform-level operations.</item>
/// </list>
/// Roles are additive: a caller with no role can do nothing but authenticate; holding a higher role does
/// NOT implicitly grant a lower one's checks, so each endpoint asserts the SPECIFIC capability it needs.
/// </remarks>
public sealed record CallerPrincipal(string CallerId, string TenantId, IReadOnlySet<string> Roles)
{
    /// <summary>Full authority across every tenant and every platform-level operation.</summary>
    public const string PlatformAdminRole = "platform-admin";

    /// <summary>May mutate tenant-scoped resources for tenants it can act for, but no platform-level ops.</summary>
    public const string GameAdminRole = "game-admin";

    /// <summary>Read-only operator: may read tenant-authorized resources; may mutate nothing.</summary>
    public const string OperatorRole = "operator";

    /// <summary>
    /// True when this caller holds the platform-admin role. Platform-scoped operations that act on fleet
    /// topology or provision tenants — not one tenant's data (e.g. draining a node, creating a tenant) —
    /// gate on this, not on <see cref="CanActFor"/> (which is tenant-scoped).
    /// </summary>
    public bool IsPlatformAdmin => Roles.Contains(PlatformAdminRole);

    /// <summary>
    /// True when this caller may MUTATE tenant-scoped resources: a game-admin or platform-admin. A
    /// read-only operator (or a caller with no admin role) is false. Tenant authority is a SEPARATE check
    /// (<see cref="CanActFor"/>): a game-admin still cannot mutate a tenant it is not scoped to.
    /// </summary>
    public bool CanMutateTenantResources => IsPlatformAdmin || Roles.Contains(GameAdminRole);

    /// <summary>True when this caller is allowed to act for <paramref name="tenantId"/>.</summary>
    public bool CanActFor(string tenantId) =>
        IsPlatformAdmin || string.Equals(TenantId, tenantId, StringComparison.Ordinal);
}
