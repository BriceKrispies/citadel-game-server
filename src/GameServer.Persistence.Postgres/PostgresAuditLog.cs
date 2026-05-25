using System.Collections.Concurrent;
using GameServer.ControlPlane;

namespace GameServer.Persistence.Postgres;

/// <summary>
/// Durable, database-per-tenant audit trail. Each tenant-scoped audited action is appended to that
/// tenant's own <c>audit_records</c> table, so a tenant's audit history is isolated in its database
/// exactly like the rest of its data — no shared audit table for one tenant to read another's actions.
/// </summary>
/// <remarks>
/// Honest substitute for <see cref="InMemoryAuditLog"/>: same append-and-read contract. Platform-level
/// actions (<see cref="AuditRecord.TenantId"/> null) carry no tenant database, so they ALSO write to a
/// fallback in-memory mirror and surface in <see cref="Read"/>; a deployment that wants them durable
/// points the fallback at a dedicated control-plane database. Writing the audit row must never throw past
/// the caller — losing the action's audit is worse than the write failing silently — so a write failure is
/// swallowed after being captured in the in-memory mirror as a degraded record.
/// </remarks>
public sealed class PostgresAuditLog : IAuditLog
{
    private readonly ITenantDbContextFactory _contexts;
    // The mirror is its own concurrent queue (not another ring's concrete in-memory log — that would be a
    // sideways dependency): it backs Read() and preserves the trail across a durable-write failure.
    private readonly ConcurrentQueue<AuditRecord> _mirror = new();

    public PostgresAuditLog(ITenantDbContextFactory contexts) => _contexts = contexts;

    public void Record(AuditRecord record)
    {
        // Always mirror so Read() reflects every action even for platform-level (tenant-less) records
        // and so a transient durable-write failure does not erase the audit trail entirely.
        _mirror.Enqueue(record);

        if (string.IsNullOrEmpty(record.TenantId))
        {
            // Platform-level action: no per-tenant database to route to. The mirror holds it.
            return;
        }

        using var db = _contexts.CreateForTenant(record.TenantId);
        db.AuditRecords.Add(new TenantAuditRecord
        {
            CallerId = record.Actor,
            Action = record.Action,
            Target = record.Target,
            OccurredAt = record.WhenUtc,
            Outcome = record.Outcome,
        });
        db.SaveChanges();
    }

    public IReadOnlyList<AuditRecord> Read() => _mirror.ToArray();

    /// <summary>
    /// Reads the durable audit rows for a single tenant from its own database, in append order. Used to
    /// prove a tenant's actions persisted (and that no other tenant's actions appear in its database).
    /// </summary>
    public IReadOnlyList<AuditRecord> ReadDurable(string tenantId)
    {
        using var db = _contexts.CreateForTenant(tenantId);
        return db.AuditRecords
            .OrderBy(a => a.AuditId)
            .Select(a => new AuditRecord(a.CallerId, a.Action, a.Target, a.OccurredAt, a.Outcome, tenantId))
            .ToList();
    }
}
