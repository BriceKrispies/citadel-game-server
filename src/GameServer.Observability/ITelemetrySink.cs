namespace GameServer.Observability;

/// <summary>
/// Sink for structured telemetry. Kept deliberately small and synchronous so the
/// hot path can emit without allocating an async state machine. Tags carry the
/// correlation context (tenant, room, session, player, trace) that every
/// meaningful operation must be attributable to.
/// </summary>
public interface ITelemetrySink
{
    /// <summary>Increments a monotonic counter (e.g. messages_in, invalid_messages).</summary>
    void Increment(string metric, IReadOnlyDictionary<string, string>? tags = null);

    /// <summary>Records a point measurement (e.g. tick_duration_ms, snapshot_size_bytes).</summary>
    void Measure(string metric, double value, IReadOnlyDictionary<string, string>? tags = null);

    /// <summary>Records a discrete structured event (e.g. connection_opened, command_rejected).</summary>
    void Event(string name, IReadOnlyDictionary<string, string>? fields = null);
}
