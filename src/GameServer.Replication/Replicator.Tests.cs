using Xunit;

namespace GameServer.Replication;

public sealed class ReplicatorTests
{
    private static EntitySnapshot Entity(string id, double x, long version = 1, int sizeBytes = 4) =>
        new(new EntityId(id), version, new RelevanceKey(x, 0, ""), new byte[sizeBytes]);

    [Fact]
    public void Replicator_Batches_OneMessagePerViewer_WithAllEntitiesInFullEveryoneMode()
    {
        var replicator = new Replicator(ReplicationPolicy.Default); // Everyone + Full
        var viewers = new[]
        {
            new Viewer(new ViewerId("v1"), RelevanceKey.None),
            new Viewer(new ViewerId("v2"), RelevanceKey.None),
            new Viewer(new ViewerId("v3"), RelevanceKey.None),
        };
        var world = new[] { Entity("a", 0), Entity("b", 1), Entity("c", 2), Entity("d", 3), Entity("e", 4) };

        var messages = replicator.Replicate(viewers, world, tick: 0);

        // Exactly one message per viewer (batching), not one per (viewer, entity).
        Assert.Equal(3, messages.Count);
        Assert.Equal(new[] { "v1", "v2", "v3" }, messages.Select(m => m.Viewer.Value).OrderBy(v => v).ToArray());
        Assert.All(messages, m => Assert.Equal(5, m.Entities.Count));     // everyone sees all 5
        Assert.All(messages, m => Assert.Equal(SnapshotMode.Full, m.Mode));
    }

    [Fact]
    public void Replicator_DeltaMode_SendsOnlyRelevantChangedEntities()
    {
        var policy = ReplicationPolicy.Default with { SnapshotMode = SnapshotMode.Delta, Interest = InterestKind.Radius, Radius = 10 };
        var replicator = new Replicator(policy);
        var viewer = new Viewer(new ViewerId("v"), new RelevanceKey(0, 0, ""));

        var first = replicator.Replicate(new[] { viewer }, new[] { Entity("near", 0), Entity("far", 100) }, tick: 0);
        var firstMsg = Assert.Single(first);
        Assert.Equal(new[] { "near" }, firstMsg.Entities.Select(e => e.Id.Value).ToArray());
        Assert.Equal(SnapshotMode.Delta, firstMsg.Mode);

        replicator.Acknowledge(viewer.Id, ackedTick: 0);

        var second = replicator.Replicate(new[] { viewer }, new[] { Entity("near", 0), Entity("far", 100) }, tick: 1);
        Assert.Empty(Assert.Single(second).Entities); // nothing changed since the ack

        var third = replicator.Replicate(new[] { viewer }, new[] { Entity("near", 0, version: 2), Entity("far", 100, version: 2) }, tick: 2);
        Assert.Equal(new[] { "near" }, Assert.Single(third).Entities.Select(e => e.Id.Value).ToArray());
    }

    [Fact]
    public void Replicator_BudgetMode_LimitsPerTick_AndEscalatesDeferredToAvoidStarvation()
    {
        // Everyone + Full, but only 10 bytes/tick: exactly one 10-byte entity fits per tick.
        var policy = ReplicationPolicy.Default with { PerClientBudgetBytes = 10 };
        var replicator = new Replicator(policy);
        var viewer = new Viewer(new ViewerId("v"), RelevanceKey.None);
        var world = new[] { Entity("e1", 0, sizeBytes: 10), Entity("e2", 1, sizeBytes: 10), Entity("e3", 2, sizeBytes: 10) };

        var sentEachTick = new List<string[]>();
        for (var tick = 0; tick < 3; tick++)
        {
            var message = Assert.Single(replicator.Replicate(new[] { viewer }, world, tick));
            sentEachTick.Add(message.Entities.Select(e => e.Id.Value).ToArray());
        }

        // Budget honored: at most one entity per tick.
        Assert.All(sentEachTick, sent => Assert.True(sent.Length <= 1, "budget must cap to one 10-byte entity per tick"));

        // Anti-starvation: across three ticks every entity is eventually served (no permanent starvation).
        var everSent = sentEachTick.SelectMany(s => s).Distinct().OrderBy(v => v).ToArray();
        Assert.Equal(new[] { "e1", "e2", "e3" }, everSent);
    }

    [Fact]
    public void Replicator_FullMode_IgnoresAcknowledgement_AndAlwaysSendsFullState()
    {
        var replicator = new Replicator(ReplicationPolicy.Default); // Full + Everyone
        var viewer = new Viewer(new ViewerId("v"), RelevanceKey.None);
        var world = new[] { Entity("a", 0), Entity("b", 1) };

        Assert.Equal(2, Assert.Single(replicator.Replicate(new[] { viewer }, world, tick: 0)).Entities.Count);

        replicator.Acknowledge(viewer.Id, ackedTick: 0);

        // Full mode does not delta against a baseline: even after an ack it resends everything.
        Assert.Equal(2, Assert.Single(replicator.Replicate(new[] { viewer }, world, tick: 1)).Entities.Count);
    }
}
