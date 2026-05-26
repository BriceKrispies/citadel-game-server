using System.Collections.Concurrent;

namespace GameServer.Persistence;

/// <summary>
/// In-memory append-only event log. Honest substitute for a durable log: appends
/// are ordered per key and reads return an immutable snapshot of that order. Also a
/// rewindable log — range reads and timeline forks (<see cref="DiscardAfter"/>) work
/// off the same sequence selector that <see cref="TruncateThrough"/> uses.
/// </summary>
public sealed class InMemoryEventLog<TKey, TEvent> : IRewindableEventLog<TKey, TEvent>
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, List<TEvent>> _streams = new();
    private readonly Func<TEvent, long>? _sequenceOf;

    /// <param name="sequenceOf">
    /// Extracts an event's monotonic sequence (for rooms, the tick). Required for
    /// <see cref="TruncateThrough"/>; if omitted, truncation is unavailable.
    /// </param>
    public InMemoryEventLog(Func<TEvent, long>? sequenceOf = null) => _sequenceOf = sequenceOf;

    public void Append(TKey key, TEvent @event)
    {
        var stream = _streams.GetOrAdd(key, _ => new List<TEvent>());
        lock (stream)
        {
            stream.Add(@event);
        }
    }

    public IReadOnlyList<TEvent> Read(TKey key)
    {
        if (!_streams.TryGetValue(key, out var stream))
        {
            return Array.Empty<TEvent>();
        }

        lock (stream)
        {
            return stream.ToArray();
        }
    }

    public void TruncateThrough(TKey key, long throughSequence)
    {
        RequireSequenceSelector(nameof(TruncateThrough));

        if (!_streams.TryGetValue(key, out var stream))
        {
            return;
        }

        lock (stream)
        {
            // Events are appended in sequence order, so retained events are a suffix: drop
            // the leading run whose sequence is at or below the watermark.
            var drop = 0;
            while (drop < stream.Count && _sequenceOf!(stream[drop]) <= throughSequence)
            {
                drop++;
            }

            if (drop > 0)
            {
                stream.RemoveRange(0, drop);
            }
        }
    }

    public IReadOnlyList<TEvent> ReadRange(TKey key, long fromExclusive, long toInclusive)
    {
        RequireSequenceSelector(nameof(ReadRange));

        if (!_streams.TryGetValue(key, out var stream))
        {
            return Array.Empty<TEvent>();
        }

        lock (stream)
        {
            // Preserve append order; the range is half-open on the low bound so the caller can
            // pass a base checkpoint's tick (its folded events excluded) and a target tick.
            var range = new List<TEvent>();
            foreach (var @event in stream)
            {
                var seq = _sequenceOf!(@event);
                if (seq > fromExclusive && seq <= toInclusive)
                {
                    range.Add(@event);
                }
            }

            return range;
        }
    }

    public void DiscardAfter(TKey key, long tick)
    {
        RequireSequenceSelector(nameof(DiscardAfter));

        if (!_streams.TryGetValue(key, out var stream))
        {
            return;
        }

        lock (stream)
        {
            // Events are appended in sequence order, so the discarded future is a suffix: drop
            // the trailing run whose sequence is strictly above the rewind point.
            var keep = stream.Count;
            while (keep > 0 && _sequenceOf!(stream[keep - 1]) > tick)
            {
                keep--;
            }

            if (keep < stream.Count)
            {
                stream.RemoveRange(keep, stream.Count - keep);
            }
        }
    }

    private void RequireSequenceSelector(string operation)
    {
        if (_sequenceOf is null)
        {
            throw new InvalidOperationException(
                $"{operation} requires a sequence selector; construct the log with one.");
        }
    }
}
