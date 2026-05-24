using System.Collections.Concurrent;

namespace GameServer.Persistence;

/// <summary>
/// In-memory latest-snapshot store. Honest substitute for a durable checkpoint
/// store: same contract, same last-write-wins semantics per key.
/// </summary>
public sealed class InMemorySnapshotStore<TKey, TSnapshot> : ISnapshotStore<TKey, TSnapshot>
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, TSnapshot> _latest = new();

    public void Save(TKey key, TSnapshot snapshot) => _latest[key] = snapshot;

    public bool TryGetLatest(TKey key, out TSnapshot snapshot) => _latest.TryGetValue(key, out snapshot!);
}
