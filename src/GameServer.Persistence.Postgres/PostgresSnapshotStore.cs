using GameServer.Simulation;
using Microsoft.EntityFrameworkCore;

namespace GameServer.Persistence.Postgres;

/// <summary>
/// Postgres-backed durable snapshot store: persists each room's latest <see cref="RoomSnapshot"/>
/// (with its replay header — seed + game-schema version) into the OWNING TENANT's database, so a
/// fresh process restores rooms after a restart. Implements the rank-0
/// <see cref="IDurableSnapshotStore{TKey,TSnapshot}"/> contract with the same last-write-wins
/// semantics as the in-memory store, so the recovery path is unchanged.
/// </summary>
/// <remarks>
/// Generic over the caller's opaque key so the adapter carries no dependency on the rank-2
/// <c>RoomKey</c>; a selector (wired at the composition root) projects the key into a
/// <see cref="DurableRoomKey"/>. The tenant part of that key selects the tenant's database via the
/// <see cref="ITenantConnectionResolver"/> — the row never lands in a shared table, so it cannot be
/// read through another tenant's connection.
/// </remarks>
public sealed class PostgresSnapshotStore<TKey> : IDurableSnapshotStore<TKey, RoomSnapshot>
    where TKey : notnull
{
    private readonly ITenantDbContextFactory _contexts;
    private readonly Func<TKey, DurableRoomKey> _keyOf;

    public PostgresSnapshotStore(ITenantDbContextFactory contexts, Func<TKey, DurableRoomKey> keyOf)
    {
        _contexts = contexts;
        _keyOf = keyOf;
    }

    public void Save(TKey key, RoomSnapshot snapshot)
    {
        var durable = _keyOf(key);
        using var db = _contexts.CreateForTenant(durable.TenantId);

        // Upsert the single latest-snapshot row for this room. The whole save is one transaction so a
        // crash mid-write leaves either the prior snapshot or the new one — never a torn row.
        using var tx = db.Database.BeginTransaction();
        var existing = db.RoomSnapshots.FirstOrDefault(s => s.RoomId == durable.RoomId);
        if (existing is null)
        {
            db.RoomSnapshots.Add(new RoomSnapshotRecord
            {
                RoomId = durable.RoomId,
                Tick = snapshot.Tick,
                Seed = snapshot.Seed,
                GameSchemaVersion = snapshot.GameSchemaVersion,
                State = snapshot.State,
            });
        }
        else
        {
            existing.Tick = snapshot.Tick;
            existing.Seed = snapshot.Seed;
            existing.GameSchemaVersion = snapshot.GameSchemaVersion;
            existing.State = snapshot.State;
        }

        db.SaveChanges();
        tx.Commit();
    }

    public bool TryGetLatest(TKey key, out RoomSnapshot snapshot)
    {
        var durable = _keyOf(key);
        using var db = _contexts.CreateForTenant(durable.TenantId);

        var record = db.RoomSnapshots.AsNoTracking().FirstOrDefault(s => s.RoomId == durable.RoomId);
        if (record is null)
        {
            snapshot = default!;
            return false;
        }

        snapshot = new RoomSnapshot(record.Tick, record.State, record.Seed, record.GameSchemaVersion);
        return true;
    }
}
