using GameServer.Protocol;
using GameServer.Simulation;
using Microsoft.EntityFrameworkCore;

namespace GameServer.Persistence.Postgres;

/// <summary>
/// Postgres-backed durable, append-only room event log: persists each room's <see cref="RoomEvent"/>
/// stream into the OWNING TENANT's database for replay-based recovery. Implements the rank-0
/// <see cref="IDurableEventLog{TKey,TEvent}"/> contract with the same ordered-append / ordered-read /
/// truncate semantics as the in-memory log.
/// </summary>
/// <remarks>
/// Generic over the caller's opaque key (no <c>RoomKey</c> dependency); the selector projects it to a
/// <see cref="DurableRoomKey"/> whose tenant part picks the tenant's database. Events for one room are
/// ordered by (tick, ordinal): the ordinal disambiguates multiple commands applied in the same tick,
/// preserving applied order on replay.
/// </remarks>
public sealed class PostgresEventLog<TKey> : IDurableEventLog<TKey, RoomEvent>
    where TKey : notnull
{
    private readonly ITenantDbContextFactory _contexts;
    private readonly Func<TKey, DurableRoomKey> _keyOf;

    public PostgresEventLog(ITenantDbContextFactory contexts, Func<TKey, DurableRoomKey> keyOf)
    {
        _contexts = contexts;
        _keyOf = keyOf;
    }

    public void Append(TKey key, RoomEvent @event)
    {
        var durable = _keyOf(key);
        using var db = _contexts.CreateForTenant(durable.TenantId);

        // The next ordinal within the event's tick, so multiple commands in one tick stay ordered and
        // an append is idempotent on the composite key (RoomId, Tick, Ordinal).
        var nextOrdinal = db.RoomEvents
            .Where(e => e.RoomId == durable.RoomId && e.Tick == @event.Tick)
            .Select(e => (int?)e.Ordinal)
            .Max() is { } max ? max + 1 : 0;

        db.RoomEvents.Add(new RoomEventRecord
        {
            RoomId = durable.RoomId,
            Tick = @event.Tick,
            Ordinal = nextOrdinal,
            PlayerId = @event.Player.Value,
            Command = @event.Command,
        });
        db.SaveChanges();
    }

    public IReadOnlyList<RoomEvent> Read(TKey key)
    {
        var durable = _keyOf(key);
        using var db = _contexts.CreateForTenant(durable.TenantId);

        return db.RoomEvents.AsNoTracking()
            .Where(e => e.RoomId == durable.RoomId)
            .OrderBy(e => e.Tick).ThenBy(e => e.Ordinal)
            .Select(e => new RoomEvent(e.Tick, new PlayerId(e.PlayerId), e.Command))
            .ToList();
    }

    public void TruncateThrough(TKey key, long throughSequence)
    {
        var durable = _keyOf(key);
        using var db = _contexts.CreateForTenant(durable.TenantId);

        // Drop every event folded into the saved snapshot (tick <= watermark). One statement, one
        // transaction (EF wraps SaveChanges) — a crash leaves the log either fully pre- or post-truncate.
        db.RoomEvents
            .Where(e => e.RoomId == durable.RoomId && e.Tick <= throughSequence)
            .ExecuteDelete();
    }
}
