namespace GameServer.Persistence;

/// <summary>
/// A snapshot store whose contents survive the process that wrote them — the durability marker
/// the platform is missing. <see cref="ISnapshotStore{TKey,TSnapshot}"/> has only an in-memory
/// implementation today, so every room's recovery checkpoint lives in the heap of one process:
/// a restart or a node failure loses all live rooms, and the (well-tested) recovery path has
/// nothing to recover from. A durable store backs the same contract with storage that outlives
/// the process (file, database, object store), so a restarted node restores rooms from it.
/// </summary>
public interface IDurableSnapshotStore<TKey, TSnapshot> : ISnapshotStore<TKey, TSnapshot>
    where TKey : notnull
{
}

/// <summary>An event log whose appended events survive the writing process. See
/// <see cref="IDurableSnapshotStore{TKey,TSnapshot}"/> for why this is required.</summary>
public interface IDurableEventLog<TKey, TEvent> : IEventLog<TKey, TEvent>
    where TKey : notnull
{
}
