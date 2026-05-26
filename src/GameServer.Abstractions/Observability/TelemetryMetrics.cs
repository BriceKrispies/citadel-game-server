namespace GameServer.Observability;

/// <summary>
/// Canonical metric and event names. Centralized so emitters and dashboards agree
/// on spelling, and so future agents discover the vocabulary in one place rather
/// than grepping for string literals.
/// </summary>
public static class TelemetryMetrics
{
    public const string ConnectionsOpened = "connections_opened";
    public const string ConnectionsClosed = "connections_closed";
    public const string MessagesIn = "messages_in";
    public const string MessagesOut = "messages_out";
    public const string InvalidMessages = "invalid_messages";
    public const string CommandsAccepted = "commands_accepted";
    public const string CommandsRejected = "commands_rejected";
    public const string BackpressureRejections = "backpressure_rejections";
    public const string CommandQueueDepth = "command_queue_depth";
    public const string AdmissionRejected = "admission_rejected";
    public const string ClientAcks = "client_acks";
    public const string TickDurationMs = "tick_duration_ms";
    public const string MissedTicks = "missed_ticks";
    public const string SnapshotsEmitted = "snapshots_emitted";
    public const string SnapshotEntities = "snapshot_entities";
    public const string RoomRestoreCount = "room_restore_count";

    /// <summary>Offline/read-only reconstructions of a room as of a past tick (the replay engine).</summary>
    public const string RoomReplayCount = "room_replay_count";

    /// <summary>Live rooms rewound to a past tick (authoritative state forked onto a new timeline).</summary>
    public const string RoomRewindCount = "room_rewind_count";

    public const string WorkerRestartCount = "worker_restart_count";
}

/// <summary>Canonical structured event names.</summary>
public static class TelemetryEvents
{
    public const string ConnectionOpened = "connection_opened";
    public const string TenantRejected = "tenant_rejected";
    public const string ProtocolRejected = "protocol_rejected";
    public const string IdentityRejected = "identity_rejected";
    public const string SessionCreated = "session_created";
    public const string CommandAccepted = "command_accepted";
    public const string RoomJoined = "room_joined";
    public const string RoomLeft = "room_left";
    public const string RoomTerminated = "room_terminated";
    public const string CommandRejected = "command_rejected";
    public const string SnapshotEmitted = "snapshot_emitted";
    public const string RoomRestored = "room_restored";

    /// <summary>A room was reconstructed as of a past tick for read-only inspection (no live mutation).</summary>
    public const string RoomReplayed = "room_replayed";

    /// <summary>A live room's authoritative state was rewound to a past tick and resumed on a new timeline.</summary>
    public const string RoomRewound = "room_rewound";

    public const string ConnectionDropped = "connection_dropped";
    public const string RoomClosed = "room_closed";
    public const string WorkerFaulted = "worker_faulted";
    public const string WorkerStopped = "worker_stopped";
    public const string WorkerDrainTimedOut = "worker_drain_timed_out";
    public const string WorkerProcessStarted = "worker_process_started";
    public const string WorkerProcessExited = "worker_process_exited";
    public const string WorkerProcessCrashed = "worker_process_crashed";
    public const string WorkerProcessKilled = "worker_process_killed";
}
