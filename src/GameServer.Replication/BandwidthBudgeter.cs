namespace GameServer.Replication;

/// <summary>An entity competing for a viewer's bandwidth this tick, with its priority.</summary>
public sealed record ReplicationCandidate(EntitySnapshot Entity, int Priority);

/// <summary>The outcome of fitting candidates into a budget: what was sent, what was deferred.</summary>
public sealed record BudgetSelection(IReadOnlyList<EntitySnapshot> Sent, IReadOnlyList<EntitySnapshot> Deferred);

/// <summary>
/// Caps per-viewer bytes per tick. Selects the highest-priority candidates whose
/// cumulative payload size fits <c>budgetBytes</c>; the rest are deferred (to be
/// retried next tick with escalated priority — see <see cref="PriorityAccumulator"/>).
/// Ties are broken by input order for determinism.
/// </summary>
public sealed class BandwidthBudgeter
{
    public BudgetSelection Select(IReadOnlyList<ReplicationCandidate> candidates, int budgetBytes)
    {
        var sent = new List<EntitySnapshot>();
        var deferred = new List<EntitySnapshot>();
        var used = 0;

        // Highest priority first; OrderByDescending is stable so equal priorities keep input order.
        foreach (var candidate in candidates.OrderByDescending(c => c.Priority))
        {
            if (used + candidate.Entity.SizeBytes <= budgetBytes)
            {
                sent.Add(candidate.Entity);
                used += candidate.Entity.SizeBytes;
            }
            else
            {
                deferred.Add(candidate.Entity);
            }
        }

        return new BudgetSelection(sent, deferred);
    }
}
