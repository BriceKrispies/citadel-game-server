using System.Collections.Concurrent;

namespace GameServer.Persistence;

/// <summary>
/// In-memory append-only event log. Honest substitute for a durable log: appends
/// are ordered per key and reads return an immutable snapshot of that order.
/// </summary>
public sealed class InMemoryEventLog<TKey, TEvent> : IEventLog<TKey, TEvent>
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
        if (_sequenceOf is null)
        {
            throw new InvalidOperationException(
                "TruncateThrough requires a sequence selector; construct the log with one.");
        }

        if (!_streams.TryGetValue(key, out var stream))
        {
            return;
        }

        lock (stream)
        {
            // Events are appended in sequence order, so retained events are a suffix: drop
            // the leading run whose sequence is at or below the watermark.
            var drop = 0;
            while (drop < stream.Count && _sequenceOf(stream[drop]) <= throughSequence)
            {
                drop++;
            }

            if (drop > 0)
            {
                stream.RemoveRange(0, drop);
            }
        }
    }
}
