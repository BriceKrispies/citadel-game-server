namespace GameServer.ControlPlane;

// PORT (rank 0). The audit trail is a port: the control plane and admin endpoints write through it, and
// adapters (in-memory, durable database-per-tenant) implement it. It lives in the universal kernel — not
// in the rank-1 ControlPlane service project — so a rank-1 durable adapter (e.g. the Postgres audit store)
// can implement it without a sideways inter-ring dependency. Per the kernel rule, the namespace stays
// GameServer.ControlPlane; the analyzer enforces by assembly, not namespace.

/// <summary>
/// One audited control-plane action: who did it, what they did, to which target, when,
/// and the outcome (e.g. "allowed"/"denied"). Admin/control operations must leave an
/// audit trail per the platform's security rules.
/// </summary>
/// <remarks>
/// <see cref="TenantId"/> names the tenant the action was scoped to (null for platform-level operations
/// that span the fleet, e.g. draining a node or provisioning a tenant). A durable, database-per-tenant
/// audit store uses it to route the row to the right tenant's database; the in-memory store ignores it.
/// </remarks>
public sealed record AuditRecord(string Actor, string Action, string Target, DateTimeOffset WhenUtc, string Outcome, string? TenantId = null);

/// <summary>Append-only audit trail for control-plane mutations.</summary>
public interface IAuditLog
{
    void Record(AuditRecord record);

    IReadOnlyList<AuditRecord> Read();
}
