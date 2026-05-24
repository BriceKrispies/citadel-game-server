namespace GameServer.Observability;

/// <summary>
/// Canonical metric and event names. Centralized so emitters and dashboards agree
/// on spelling, and so future agents discover the vocabulary in one place rather
/// than grepping for string literals.
/// </summary>
public static class TelemetryMetrics
{
    public const string ConnectionsOpened = "connections_opened";
    public const string MessagesIn = "messages_in";
    public const string MessagesOut = "messages_out";
    public const string InvalidMessages = "invalid_messages";
    public const string CommandsAccepted = "commands_accepted";
    public const string CommandsRejected = "commands_rejected";
    public const string ClientAcks = "client_acks";
    public const string TickDurationMs = "tick_duration_ms";
    public const string MissedTicks = "missed_ticks";
    public const string SnapshotsEmitted = "snapshots_emitted";
    public const string SnapshotEntities = "snapshot_entities";
    public const string RoomRestoreCount = "room_restore_count";
}

/// <summary>Canonical structured event names.</summary>
public static class TelemetryEvents
{
    public const string ConnectionOpened = "connection_opened";
    public const string TenantRejected = "tenant_rejected";
    public const string ProtocolRejected = "protocol_rejected";
    public const string IdentityRejected = "identity_rejected";
    public const string SessionCreated = "session_created";
    public const string RoomJoined = "room_joined";
    public const string CommandRejected = "command_rejected";
    public const string SnapshotEmitted = "snapshot_emitted";
    public const string RoomRestored = "room_restored";
    public const string ConnectionDropped = "connection_dropped";
}
