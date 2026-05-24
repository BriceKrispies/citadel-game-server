using System.Collections.Concurrent;

namespace GameServer.ControlPlane;

/// <summary>
/// One audited control-plane action: who did it, what they did, to which target, when,
/// and the outcome (e.g. "allowed"/"denied"). Admin/control operations must leave an
/// audit trail per the platform's security rules.
/// </summary>
public sealed record AuditRecord(string Actor, string Action, string Target, DateTimeOffset WhenUtc, string Outcome);

/// <summary>Append-only audit trail for control-plane mutations.</summary>
public interface IAuditLog
{
    void Record(AuditRecord record);

    IReadOnlyList<AuditRecord> Read();
}

/// <summary>
/// In-memory audit trail (per process). Durable storage is a future swap behind
/// <see cref="IAuditLog"/>; the contract — record-and-read in append order — does not
/// change.
/// </summary>
public sealed class InMemoryAuditLog : IAuditLog
{
    private readonly ConcurrentQueue<AuditRecord> _records = new();

    public void Record(AuditRecord record) => _records.Enqueue(record);

    public IReadOnlyList<AuditRecord> Read() => _records.ToArray();
}
