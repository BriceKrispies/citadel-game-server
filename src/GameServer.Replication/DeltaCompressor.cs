namespace GameServer.Replication;

/// <summary>
/// Per-viewer delta compression against an acknowledged baseline (the Quake/Source
/// model). <see cref="Compute"/> returns only entities that are new or whose version
/// differs from the viewer's acknowledged baseline, and records the full set it sent
/// at that tick. <see cref="Acknowledge"/> advances the baseline to the snapshot the
/// client actually confirmed — the latest one at or before the acked tick — so the
/// server never assumes the client holds state it has not yet acknowledged. Until a
/// snapshot is acked, its changed entities keep being recomputed, so dropped frames
/// self-heal. Baselines and pending snapshots are tracked independently per viewer.
/// </summary>
/// <remarks>
/// The baseline advances on a client ack, never on a successful send: a write that
/// reaches the socket is not proof the client received and applied the frame.
/// </remarks>
public sealed class DeltaCompressor
{
    /// <summary>Default cap on retained unacknowledged snapshots per viewer (see the constructor).</summary>
    public const int DefaultMaxUnackedSnapshots = 64;

    private readonly int _maxUnackedSnapshots;

    // Guards _baseline and _pending: Compute runs on the tick thread while Acknowledge/
    // Forget run on connection threads, and an admin observer reads PendingSnapshotCount
    // from yet another thread. Per-room and not hot, so a single lock is ample.
    private readonly object _lock = new();

    // What each viewer has confirmed it holds.
    private readonly Dictionary<ViewerId, Dictionary<EntityId, long>> _baseline = new();
    // Snapshots sent but not yet acknowledged, keyed (ascending) by the tick they were
    // computed for, so an ack can commit exactly the state the client confirmed rather
    // than whatever the most recent tick happens to hold.
    private readonly Dictionary<ViewerId, SortedDictionary<long, Dictionary<EntityId, long>>> _pending = new();

    /// <param name="maxUnackedSnapshots">
    /// The most snapshots retained per viewer awaiting acknowledgement. Bounds memory
    /// against a client that stops acking: beyond this window the oldest unacked
    /// snapshots are dropped (the entities they held keep being resent). Must be ≥ 1.
    /// </param>
    public DeltaCompressor(int maxUnackedSnapshots = DefaultMaxUnackedSnapshots)
    {
        if (maxUnackedSnapshots < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxUnackedSnapshots), maxUnackedSnapshots, "At least one unacked snapshot must be retained.");
        }

        _maxUnackedSnapshots = maxUnackedSnapshots;
    }

    /// <summary>Returns the entities new/changed since this viewer's acknowledged baseline.</summary>
    public IReadOnlyList<EntitySnapshot> Compute(ViewerId viewer, IReadOnlyList<EntitySnapshot> relevant, long tick)
    {
        lock (_lock)
        {
            _baseline.TryGetValue(viewer, out var baseline);

            var changed = new List<EntitySnapshot>();
            foreach (var entity in relevant)
            {
                // New entity, or its authoritative version differs from what the viewer has acked.
                if (baseline is null || !baseline.TryGetValue(entity.Id, out var ackedVersion) || ackedVersion != entity.Version)
                {
                    changed.Add(entity);
                }
            }

            if (!_pending.TryGetValue(viewer, out var byTick))
            {
                byTick = new SortedDictionary<long, Dictionary<EntityId, long>>();
                _pending[viewer] = byTick;
            }

            // Remember the full relevant set sent this tick so a later ack can commit it exactly.
            byTick[tick] = relevant.ToDictionary(e => e.Id, e => e.Version);

            // Bound the history: a client that never acks must not cost one retained
            // snapshot per tick forever. Drop the oldest unacked snapshots beyond the
            // window. The entities they held remain "changed" vs the baseline, so they
            // keep being resent — nothing is lost; only the horizon for a precise ack of
            // an old tick shortens (such an ack then commits nothing, which is safe).
            while (byTick.Count > _maxUnackedSnapshots)
            {
                byTick.Remove(byTick.Keys.First());
            }

            return changed;
        }
    }

    /// <summary>
    /// Advances the viewer's baseline to the snapshot it confirmed (the latest sent at
    /// or before <paramref name="ackedTick"/>) and discards every unacknowledged
    /// snapshot up to and including it. Acks for ticks never sent are ignored.
    /// </summary>
    public void Acknowledge(ViewerId viewer, long ackedTick)
    {
        lock (_lock)
        {
            if (!_pending.TryGetValue(viewer, out var byTick))
            {
                return;
            }

            long? commitTick = null;
            foreach (var tick in byTick.Keys)
            {
                if (tick > ackedTick)
                {
                    break; // keys ascend: no later tick can qualify.
                }

                commitTick = tick;
            }

            if (commitTick is null)
            {
                return;
            }

            _baseline[viewer] = new Dictionary<EntityId, long>(byTick[commitTick.Value]);

            foreach (var tick in byTick.Keys.Where(t => t <= commitTick.Value).ToList())
            {
                byTick.Remove(tick);
            }
        }
    }

    /// <summary>
    /// Forgets a viewer's baseline and any unacknowledged snapshots, so the next
    /// <see cref="Compute"/> resends full state. Called when a client (re)subscribes:
    /// a reconnected client cannot be assumed to still hold state a previous
    /// connection acknowledged.
    /// </summary>
    public void Forget(ViewerId viewer)
    {
        lock (_lock)
        {
            _baseline.Remove(viewer);
            _pending.Remove(viewer);
        }
    }

    /// <summary>
    /// The number of snapshots sent to a viewer but not yet acknowledged. It rises by
    /// one each tick the client fails to ack and only falls on <see cref="Acknowledge"/>
    /// or <see cref="Forget"/>: the server must retain every unacked snapshot to delta
    /// against the one the client eventually confirms. A backlog that climbs without
    /// bound is the signal a client has stopped acking and should be shed (backpressure).
    /// </summary>
    public int PendingSnapshotCount(ViewerId viewer)
    {
        lock (_lock)
        {
            return _pending.TryGetValue(viewer, out var byTick) ? byTick.Count : 0;
        }
    }
}
