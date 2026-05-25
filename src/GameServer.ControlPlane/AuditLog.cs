using System.Collections.Concurrent;

namespace GameServer.ControlPlane;

// The IAuditLog port and the AuditRecord DTO are in the universal kernel (GameServer.Abstractions) so a
// durable rank-1 adapter can implement the port without a sideways ring dependency. This file holds only
// the in-memory adapter.

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
