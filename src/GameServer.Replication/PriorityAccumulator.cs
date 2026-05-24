namespace GameServer.Replication;

/// <summary>
/// Anti-starvation for the bandwidth budget. Tracks per-(viewer, entity) staleness:
/// each tick an entity is deferred raises its effective priority, so a low-priority
/// entity that keeps losing the budget eventually wins. Sending an entity resets it.
/// </summary>
public sealed class PriorityAccumulator
{
    private readonly Dictionary<(ViewerId Viewer, EntityId Entity), int> _staleness = new();

    /// <summary>Effective priority = <paramref name="basePriority"/> + accumulated staleness.</summary>
    public int Priority(ViewerId viewer, EntityId entity, int basePriority) =>
        basePriority + (_staleness.TryGetValue((viewer, entity), out var staleness) ? staleness : 0);

    /// <summary>Marks the entity as sent to the viewer this tick (resets staleness).</summary>
    public void OnSent(ViewerId viewer, EntityId entity) => _staleness[(viewer, entity)] = 0;

    /// <summary>Marks the entity as deferred for the viewer this tick (increments staleness).</summary>
    public void OnDeferred(ViewerId viewer, EntityId entity)
    {
        _staleness.TryGetValue((viewer, entity), out var staleness);
        _staleness[(viewer, entity)] = staleness + 1;
    }
}
