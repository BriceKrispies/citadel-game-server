using Microsoft.EntityFrameworkCore;

namespace GameServer.Persistence.Postgres;

/// <summary>
/// EF Core context for ONE tenant's database. Every row here belongs to the single tenant whose
/// connection string opened the context — there is no <c>TenantId</c> discriminator column and no
/// cross-tenant table, so a query cannot, even by mistake, return another tenant's data. This is the
/// database-per-tenant isolation boundary in code form.
/// </summary>
/// <remarks>
/// Durable records (per the platform's persistence rules): the tenant's games and their versions
/// (the schema-version registry that a room snapshot's <c>GameSchemaVersion</c> references), rooms,
/// sessions, an audit trail, and the room replay artifacts (latest snapshot + append-only events).
/// </remarks>
public sealed class TenantDbContext : DbContext
{
    public TenantDbContext(DbContextOptions<TenantDbContext> options) : base(options)
    {
    }

    public DbSet<GameRecord> Games => Set<GameRecord>();
    public DbSet<GameVersionRecord> GameVersions => Set<GameVersionRecord>();
    public DbSet<RoomRecord> Rooms => Set<RoomRecord>();
    public DbSet<SessionRecord> Sessions => Set<SessionRecord>();
    public DbSet<TenantAuditRecord> AuditRecords => Set<TenantAuditRecord>();
    public DbSet<RoomSnapshotRecord> RoomSnapshots => Set<RoomSnapshotRecord>();
    public DbSet<RoomEventRecord> RoomEvents => Set<RoomEventRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GameRecord>(e =>
        {
            e.ToTable("games");
            e.HasKey(g => g.GameId);
        });

        modelBuilder.Entity<GameVersionRecord>(e =>
        {
            e.ToTable("game_versions");
            e.HasKey(v => new { v.GameId, v.SchemaVersion });
        });

        modelBuilder.Entity<RoomRecord>(e =>
        {
            e.ToTable("rooms");
            e.HasKey(r => r.RoomId);
        });

        modelBuilder.Entity<SessionRecord>(e =>
        {
            e.ToTable("sessions");
            e.HasKey(s => s.SessionId);
        });

        modelBuilder.Entity<TenantAuditRecord>(e =>
        {
            e.ToTable("audit_records");
            e.HasKey(a => a.AuditId);
        });

        // Latest snapshot per room: the recovery checkpoint. One row per room (RoomId is the PK),
        // so a save is an upsert and last-write-wins — the same contract the in-memory store gives.
        modelBuilder.Entity<RoomSnapshotRecord>(e =>
        {
            e.ToTable("room_snapshots");
            e.HasKey(s => s.RoomId);
        });

        // Append-only event log per room, ordered by (RoomId, Tick, Ordinal). The composite key makes
        // an event idempotent on re-append and lets recovery read a room's stream in order.
        modelBuilder.Entity<RoomEventRecord>(e =>
        {
            e.ToTable("room_events");
            e.HasKey(ev => new { ev.RoomId, ev.Tick, ev.Ordinal });
        });
    }
}
