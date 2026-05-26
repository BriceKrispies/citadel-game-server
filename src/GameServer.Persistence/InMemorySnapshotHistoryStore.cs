using System.Collections.Concurrent;

namespace GameServer.Persistence;

/// <summary>
/// In-memory snapshot history store. Honest substitute for a durable checkpoint history: it keeps
/// every saved checkpoint per key (ordered by tick) instead of last-write-wins, so a room can be
/// rebuilt as of any retained past tick. The tick of a snapshot is read via the selector supplied
/// at construction, keeping the port free of any tick concept on <typeparamref name="TSnapshot"/>.
/// </summary>
public sealed class InMemorySnapshotHistoryStore<TKey, TSnapshot> : ISnapshotHistoryStore<TKey, TSnapshot>
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, SortedList<long, TSnapshot>> _histories = new();
    private readonly Func<TSnapshot, long> _tickOf;

    /// <param name="tickOf">Extracts a checkpoint's tick (its position in history).</param>
    public InMemorySnapshotHistoryStore(Func<TSnapshot, long> tickOf) =>
        _tickOf = tickOf ?? throw new ArgumentNullException(nameof(tickOf));

    public void Save(TKey key, TSnapshot snapshot)
    {
        var history = _histories.GetOrAdd(key, _ => new SortedList<long, TSnapshot>());
        lock (history)
        {
            // Keyed by tick so a re-save at the same tick (e.g. a fresh checkpoint written when a
            // room is rewound to that point) replaces rather than duplicates.
            history[_tickOf(snapshot)] = snapshot;
        }
    }

    public bool TryGetLatest(TKey key, out TSnapshot snapshot)
    {
        if (_histories.TryGetValue(key, out var history))
        {
            lock (history)
            {
                if (history.Count > 0)
                {
                    snapshot = history.Values[history.Count - 1];
                    return true;
                }
            }
        }

        snapshot = default!;
        return false;
    }

    public bool TryGetLatestAtOrBefore(TKey key, long tick, out TSnapshot snapshot)
    {
        if (_histories.TryGetValue(key, out var history))
        {
            lock (history)
            {
                // SortedList keys are ascending; walk from the newest down to the first at or below tick.
                for (var i = history.Count - 1; i >= 0; i--)
                {
                    if (history.Keys[i] <= tick)
                    {
                        snapshot = history.Values[i];
                        return true;
                    }
                }
            }
        }

        snapshot = default!;
        return false;
    }

    public IReadOnlyList<long> ListCheckpointTicks(TKey key)
    {
        if (!_histories.TryGetValue(key, out var history))
        {
            return Array.Empty<long>();
        }

        lock (history)
        {
            return history.Keys.ToArray();
        }
    }

    public void PruneThrough(TKey key, long throughTick)
    {
        if (!_histories.TryGetValue(key, out var history))
        {
            return;
        }

        lock (history)
        {
            // Keep the single newest checkpoint at or below the watermark — it is the restore base for
            // the oldest reachable tick — and drop everything strictly older. Checkpoints after the
            // watermark are untouched.
            var floorIndex = -1;
            for (var i = history.Count - 1; i >= 0; i--)
            {
                if (history.Keys[i] <= throughTick)
                {
                    floorIndex = i;
                    break;
                }
            }

            for (var i = floorIndex - 1; i >= 0; i--)
            {
                history.RemoveAt(i);
            }
        }
    }
}
