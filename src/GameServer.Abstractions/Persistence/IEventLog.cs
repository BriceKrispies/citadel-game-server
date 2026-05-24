namespace GameServer.Persistence;

/// <summary>
/// Append-only log of events per key. Generic over key and event type so
/// Persistence stays free of inward dependencies. This is the replay seam: the
/// ordered events for a key reconstruct how a room reached its state.
/// </summary>
public interface IEventLog<TKey, TEvent>
    where TKey : notnull
{
    void Append(TKey key, TEvent @event);

    IReadOnlyList<TEvent> Read(TKey key);

    /// <summary>
    /// Drops events whose sequence is at or below <paramref name="throughSequence"/> — they
    /// have been folded into a durable snapshot and are no longer needed for recovery. This
    /// is what bounds an otherwise append-only log; without it the log grows forever. The
    /// "sequence" is the caller's monotonic ordering (for rooms, the tick).
    /// </summary>
    void TruncateThrough(TKey key, long throughSequence);
}
