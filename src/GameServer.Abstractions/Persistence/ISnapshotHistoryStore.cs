namespace GameServer.Persistence;

/// <summary>
/// A snapshot store that retains a HISTORY of checkpoints per key — not just the latest — so a
/// room can be rebuilt as of any past tick still inside the retained window. This is the rewind
/// seam on the snapshot side: <see cref="TryGetLatestAtOrBefore"/> finds the newest checkpoint not
/// after a target tick, which becomes the base a replay extends forward to that tick.
/// </summary>
/// <remarks>
/// It is-a <see cref="ISnapshotStore{TKey,TSnapshot}"/>: <c>Save</c> records a checkpoint and
/// <c>TryGetLatest</c> returns the newest one, so any latest-only consumer keeps working unchanged.
/// A history store knows each snapshot's tick via a selector supplied to the concrete implementation
/// (the port stays free of any tick concept the caller's snapshot type might not expose).
/// </remarks>
public interface ISnapshotHistoryStore<TKey, TSnapshot> : ISnapshotStore<TKey, TSnapshot>
    where TKey : notnull
{
    /// <summary>
    /// Finds the newest stored checkpoint whose tick is at or before <paramref name="tick"/>. Returns
    /// false when none exists at or before that tick — either the room has no history, or every
    /// checkpoint that old has been pruned past the rewind horizon (so that point is no longer
    /// reachable). The found snapshot is the base for replaying forward to a target tick.
    /// </summary>
    bool TryGetLatestAtOrBefore(TKey key, long tick, out TSnapshot snapshot);

    /// <summary>Ticks of all retained checkpoints for a key, ascending. For tooling/diagnostics.</summary>
    IReadOnlyList<long> ListCheckpointTicks(TKey key);

    /// <summary>
    /// Drops checkpoints whose tick is at or below <paramref name="throughTick"/> — they fall outside
    /// the rewind horizon. Always keeps the single newest checkpoint at or below the watermark, so a
    /// room never loses its restore base. This is what bounds an otherwise unbounded history.
    /// </summary>
    void PruneThrough(TKey key, long throughTick);
}
