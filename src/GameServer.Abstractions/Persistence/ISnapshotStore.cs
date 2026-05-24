namespace GameServer.Persistence;

/// <summary>
/// Stores the latest durable snapshot for a key. Generic over key and snapshot so
/// Persistence carries no dependency on Simulation or Protocol types — callers
/// supply their own. This is the recovery-checkpoint seam: a room can later be
/// restored from its most recent snapshot.
/// </summary>
public interface ISnapshotStore<TKey, TSnapshot>
    where TKey : notnull
{
    void Save(TKey key, TSnapshot snapshot);

    bool TryGetLatest(TKey key, out TSnapshot snapshot);
}
