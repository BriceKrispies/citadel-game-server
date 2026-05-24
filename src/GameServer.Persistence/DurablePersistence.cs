namespace GameServer.Persistence;

/// <summary>
/// A snapshot store whose contents survive the process that wrote them — the durability marker
/// the platform is missing. <see cref="ISnapshotStore{TKey,TSnapshot}"/> has only an in-memory
/// implementation today, so every room's recovery checkpoint lives in the heap of one process:
/// a restart or a node failure loses all live rooms, and the (well-tested) recovery path has
/// nothing to recover from. A durable store backs the same contract with storage that outlives
/// the process (file, database, object store), so a restarted node restores rooms from it.
/// </summary>
/// <remarks>
/// RED-phase seam: the marker and a stub implementation exist so restart-recovery can be pinned
/// by a test (<c>DurableRestartScenario</c>); no durable backing is implemented yet.
/// </remarks>
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

/// <summary>
/// File-backed durable snapshot store. Persists each key's latest snapshot under a directory so
/// a new instance pointed at the same directory (i.e. after a restart) reads what an earlier
/// instance wrote.
/// </summary>
public sealed class FileSnapshotStore<TKey, TSnapshot> : IDurableSnapshotStore<TKey, TSnapshot>
    where TKey : notnull
{
    private const string NotBuilt =
        "FileSnapshotStore is a RED-phase seam: durable (restart-surviving) snapshot persistence is not implemented yet.";

    public FileSnapshotStore(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("A storage directory is required.", nameof(directory));
        }

        _directory = directory;
    }

    private readonly string _directory;

    public void Save(TKey key, TSnapshot snapshot) => throw new NotImplementedException(NotBuilt);

    public bool TryGetLatest(TKey key, out TSnapshot snapshot) => throw new NotImplementedException(NotBuilt);
}
