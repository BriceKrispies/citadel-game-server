namespace GameServer.Replication;

/// <summary>
/// The per-viewer payload produced for a tick: the entities to send and whether they
/// are a full snapshot or a delta. Exactly one message is produced per viewer
/// (batching), regardless of how many entities it contains.
/// </summary>
public sealed record ReplicationMessage(ViewerId Viewer, SnapshotMode Mode, IReadOnlyList<EntitySnapshot> Entities);

/// <summary>
/// The replication pipeline. For each viewer it applies the policy's interest filter,
/// optional delta compression against the viewer's baseline, and optional bandwidth
/// budget (with priority accumulation), then emits a single batched
/// <see cref="ReplicationMessage"/>. This is what replaces the O(N²) per-player
/// fan-out with one policy-shaped message per viewer.
/// </summary>
public sealed class Replicator
{
    private const int BasePriority = 0;

    private readonly ReplicationPolicy _policy;
    private readonly IInterestStrategy _interest;
    private readonly DeltaCompressor _delta = new();
    private readonly BandwidthBudgeter _budgeter = new();
    private readonly PriorityAccumulator _priority = new();

    public Replicator(ReplicationPolicy policy)
    {
        _policy = policy;
        _interest = policy.CreateInterestStrategy();
    }

    public ReplicationPolicy Policy => _policy;

    /// <summary>
    /// Produces exactly one message per viewer for the given world state at
    /// <paramref name="tick"/>. The tick tags each delta snapshot so a later client
    /// ack can advance the baseline to precisely the state it confirmed.
    /// </summary>
    public IReadOnlyList<ReplicationMessage> Replicate(IReadOnlyList<Viewer> viewers, IReadOnlyList<EntitySnapshot> world, long tick)
    {
        var messages = new List<ReplicationMessage>(viewers.Count);

        foreach (var viewer in viewers)
        {
            // 1) Interest: only entities relevant to this viewer.
            var relevant = _interest.Relevant(viewer, world);

            // 2) Delta: in Delta mode, only what changed since the viewer's acknowledged baseline.
            var candidates = _policy.SnapshotMode == SnapshotMode.Delta
                ? _delta.Compute(viewer.Id, relevant, tick)
                : relevant;

            // 3) Budget: if configured, cap bytes/tick by priority (deferred entities escalate).
            IReadOnlyList<EntitySnapshot> entities;
            if (_policy.PerClientBudgetBytes > 0)
            {
                var ranked = candidates
                    .Select(e => new ReplicationCandidate(e, _priority.Priority(viewer.Id, e.Id, BasePriority)))
                    .ToList();
                var selection = _budgeter.Select(ranked, _policy.PerClientBudgetBytes);

                foreach (var entity in selection.Sent)
                {
                    _priority.OnSent(viewer.Id, entity.Id);
                }

                foreach (var entity in selection.Deferred)
                {
                    _priority.OnDeferred(viewer.Id, entity.Id);
                }

                entities = selection.Sent;
            }
            else
            {
                entities = candidates;
            }

            messages.Add(new ReplicationMessage(viewer.Id, _policy.SnapshotMode, entities));
        }

        return messages;
    }

    /// <summary>
    /// Acknowledges a viewer's confirmed receipt of state through <paramref name="ackedTick"/>,
    /// advancing its delta baseline. Driven by a client ack, never by a successful send.
    /// </summary>
    public void Acknowledge(ViewerId viewer, long ackedTick) => _delta.Acknowledge(viewer, ackedTick);

    /// <summary>
    /// Resets a viewer's delta baseline so the next tick resends full state. Called
    /// when a client (re)subscribes: a reconnected client may have lost everything a
    /// previous connection acknowledged, so the server must re-establish a keyframe.
    /// </summary>
    public void Resubscribe(ViewerId viewer) => _delta.Forget(viewer);
}
