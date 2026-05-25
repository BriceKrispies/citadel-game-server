namespace GameServer.Persistence.Postgres;

/// <summary>
/// The durable EF entity model for a single tenant's database. These are mutable persistence
/// records (EF materializes them), kept deliberately flat: the platform's rich domain/protocol
/// types stay in their own layers; this is only what survives a restart on disk.
/// </summary>
/// <remarks>
/// All records here are implicitly scoped to one tenant by the database they live in (see
/// <see cref="TenantDbContext"/>): there is no tenant-id column, because there is no shared table
/// for one to disambiguate.
/// </remarks>
public sealed class GameRecord
{
    public string GameId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>The game version currently in effect for new rooms of this game.</summary>
    public int CurrentSchemaVersion { get; set; }
}

/// <summary>A registered schema version of a game — the durable game-version record. A room snapshot's
/// captured <c>GameSchemaVersion</c> references one of these so a restore can detect an incompatible
/// game build instead of mis-deserializing.</summary>
public sealed class GameVersionRecord
{
    public string GameId { get; set; } = string.Empty;
    public int SchemaVersion { get; set; }
    public string Notes { get; set; } = string.Empty;
}

/// <summary>A durable room record. Captures which game (and version) a room runs so recovery knows
/// how to interpret its snapshot/events.</summary>
public sealed class RoomRecord
{
    public string RoomId { get; set; } = string.Empty;
    public string GameId { get; set; } = string.Empty;
    public int GameSchemaVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A durable session record (player session lifecycle in this tenant).</summary>
public sealed class SessionRecord
{
    public string SessionId { get; set; } = string.Empty;
    public string PlayerId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
}

/// <summary>A durable audit entry for this tenant — who did what, when, and whether it was allowed.
/// Named distinctly from the control-plane audit DTO; this is the on-disk EF row.</summary>
public sealed class TenantAuditRecord
{
    public long AuditId { get; set; }
    public string CallerId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public string Outcome { get; set; } = string.Empty;
}

/// <summary>The latest authoritative snapshot for a room — the recovery checkpoint, including the
/// replay header (seed + game-schema version) captured from <c>RoomSnapshot</c>.</summary>
public sealed class RoomSnapshotRecord
{
    public string RoomId { get; set; } = string.Empty;
    public long Tick { get; set; }
    public int Seed { get; set; }
    public int GameSchemaVersion { get; set; }

    /// <summary>The opaque game-serialized state. The platform never interprets it.</summary>
    public byte[] State { get; set; } = Array.Empty<byte>();
}

/// <summary>One appended room event in the append-only replay log. <see cref="Ordinal"/> orders
/// events within a tick so multiple commands in one tick keep their applied order.</summary>
public sealed class RoomEventRecord
{
    public string RoomId { get; set; } = string.Empty;
    public long Tick { get; set; }
    public int Ordinal { get; set; }
    public string PlayerId { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
}
