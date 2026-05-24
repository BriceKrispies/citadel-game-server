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
}
