namespace GameServer.Persistence;

/// <summary>
/// Append-only log of events per key. Generic over key and event type so
/// Persistence stays free of inward dependencies. This is the replay seam: the
/// ordered events for a key reconstruct how a room reached its state.
/// </summary>
public interface IEventLog<TKey, TEvent>
    where TKey : notnull
{
    void Append(TKey key, TEvent @event);

    IReadOnlyList<TEvent> Read(TKey key);
}
