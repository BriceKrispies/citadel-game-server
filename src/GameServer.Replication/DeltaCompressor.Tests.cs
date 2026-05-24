using Xunit;

namespace GameServer.Replication;

public sealed class DeltaCompressorTests
{
    private static EntitySnapshot Entity(string id, long version) =>
        new(new EntityId(id), version, RelevanceKey.None, new byte[4]);

    private static string[] Ids(IEnumerable<EntitySnapshot> entities) =>
        entities.Select(e => e.Id.Value).OrderBy(v => v).ToArray();

    [Fact]
    public void Delta_FirstReplication_ReturnsAllEntities()
    {
        var delta = new DeltaCompressor();
        var viewer = new ViewerId("v");

        var changed = delta.Compute(viewer, new[] { Entity("a", 1), Entity("b", 1) }, tick: 1);

        Assert.Equal(new[] { "a", "b" }, Ids(changed)); // no baseline yet → everything is "changed"
    }

    [Fact]
    public void Delta_AfterAcknowledge_ReturnsOnlyChangedEntities()
    {
        var delta = new DeltaCompressor();
        var viewer = new ViewerId("v");

        delta.Compute(viewer, new[] { Entity("a", 1), Entity("b", 1) }, tick: 1);
        delta.Acknowledge(viewer, ackedTick: 1); // baseline is now {a:1, b:1}

        Assert.Empty(delta.Compute(viewer, new[] { Entity("a", 1), Entity("b", 1) }, tick: 2));
        Assert.Equal(new[] { "a" }, Ids(delta.Compute(viewer, new[] { Entity("a", 2), Entity("b", 1) }, tick: 3)));
    }

    [Fact]
    public void Delta_WithoutAcknowledge_ResendsUntilAcked()
    {
        var delta = new DeltaCompressor();
        var viewer = new ViewerId("v");

        var first = delta.Compute(viewer, new[] { Entity("a", 1) }, tick: 1);
        var second = delta.Compute(viewer, new[] { Entity("a", 1) }, tick: 2); // no Acknowledge between

        Assert.Equal(new[] { "a" }, Ids(first));
        Assert.Equal(new[] { "a" }, Ids(second)); // unacked → resent, not dropped
    }

    [Fact]
    public void Delta_BaselinesAreIndependentPerViewer()
    {
        var delta = new DeltaCompressor();

        delta.Compute(new ViewerId("v1"), new[] { Entity("a", 1) }, tick: 1);
        delta.Acknowledge(new ViewerId("v1"), ackedTick: 1); // only v1 has acked

        // v2 has its own (empty) baseline, so it must still receive 'a' in full.
        var v2 = delta.Compute(new ViewerId("v2"), new[] { Entity("a", 1) }, tick: 1);
        Assert.Equal(new[] { "a" }, Ids(v2));
    }

    [Fact]
    public void Delta_AckCommitsTheConfirmedTicksSet()
    {
        var delta = new DeltaCompressor();
        var viewer = new ViewerId("v");

        delta.Compute(viewer, new[] { Entity("a", 1) }, tick: 1);                 // sent {a} at tick 1
        delta.Compute(viewer, new[] { Entity("a", 1), Entity("b", 1) }, tick: 2); // sent {a,b} at tick 2
        delta.Acknowledge(viewer, ackedTick: 2);                                  // client confirms tick 2

        Assert.Empty(delta.Compute(viewer, new[] { Entity("a", 1), Entity("b", 1) }, tick: 3));
    }

    [Fact]
    public void Delta_AckOfOlderTick_DoesNotCommitNewerUnackedState()
    {
        var delta = new DeltaCompressor();
        var viewer = new ViewerId("v");

        delta.Compute(viewer, new[] { Entity("a", 1) }, tick: 1); // sent a:1 at tick 1
        delta.Compute(viewer, new[] { Entity("a", 2) }, tick: 2); // sent a:2 at tick 2 (still unacked)

        delta.Acknowledge(viewer, ackedTick: 1); // client only confirms tick 1 (it never got tick 2)

        // Baseline must be a:1, so a:2 is still "changed" and must be resent — the
        // server must not assume the client holds the newer, unacknowledged state.
        Assert.Equal(new[] { "a" }, Ids(delta.Compute(viewer, new[] { Entity("a", 2) }, tick: 3)));

        // Once the client confirms tick 2, a:2 is baselined and no longer resent.
        delta.Acknowledge(viewer, ackedTick: 2);
        Assert.Empty(delta.Compute(viewer, new[] { Entity("a", 2) }, tick: 4));
    }

    [Fact]
    public void Delta_AckForUnsentTick_IsIgnored()
    {
        var delta = new DeltaCompressor();
        var viewer = new ViewerId("v");

        delta.Compute(viewer, new[] { Entity("a", 1) }, tick: 5);
        delta.Acknowledge(viewer, ackedTick: 1); // nothing was sent at or before tick 1

        // Baseline unchanged (still empty), so 'a' keeps being sent.
        Assert.Equal(new[] { "a" }, Ids(delta.Compute(viewer, new[] { Entity("a", 1) }, tick: 6)));
    }

    [Fact]
    public void Delta_RemovedEntity_IsNotResent()
    {
        var delta = new DeltaCompressor();
        var viewer = new ViewerId("v");

        delta.Compute(viewer, new[] { Entity("a", 1), Entity("b", 1) }, tick: 1);
        delta.Acknowledge(viewer, ackedTick: 1);

        // 'b' disappears from the relevant set; 'a' is unchanged → nothing to send.
        Assert.Empty(delta.Compute(viewer, new[] { Entity("a", 1) }, tick: 2));
    }

    [Fact]
    public void Delta_VersionDifference_IncludingRegression_IsSent()
    {
        var delta = new DeltaCompressor();
        var viewer = new ViewerId("v");

        delta.Compute(viewer, new[] { Entity("a", 2) }, tick: 1);
        delta.Acknowledge(viewer, ackedTick: 1); // baseline a:2

        // Authoritative version differs from baseline (even though it decreased) → reconcile.
        Assert.Equal(new[] { "a" }, Ids(delta.Compute(viewer, new[] { Entity("a", 1) }, tick: 2)));
    }

    [Fact]
    public void Delta_ClientThatNeverAcks_CapsTheUnackedBacklog()
    {
        const int window = 4;
        var delta = new DeltaCompressor(maxUnackedSnapshots: window);
        var viewer = new ViewerId("v");

        Assert.Equal(0, delta.PendingSnapshotCount(viewer));

        // The client goes silent while the server keeps ticking. The unacked backlog
        // must NOT grow one snapshot per tick forever — that is a memory leak driven
        // by a misbehaving client. It grows up to the retention window and then stays
        // capped (the oldest unacked snapshots are dropped). The still-unacked entity
        // remains "changed" vs the baseline, so it keeps being resent — nothing is
        // lost; only the horizon for a precise older ack shortens.
        for (var tick = 1; tick <= window * 5; tick++)
        {
            var changed = delta.Compute(viewer, new[] { Entity("a", 1) }, tick);

            Assert.Equal(new[] { "a" }, Ids(changed)); // unacked → still resent every tick
            Assert.Equal(Math.Min(tick, window), delta.PendingSnapshotCount(viewer)); // grows, then capped
        }

        // An ack within the retained window still collapses the whole backlog.
        delta.Acknowledge(viewer, ackedTick: window * 5);
        Assert.Equal(0, delta.PendingSnapshotCount(viewer));

        // Forgetting the viewer (disconnect/resubscribe) also clears it.
        delta.Compute(viewer, new[] { Entity("a", 1) }, tick: 100);
        delta.Forget(viewer);
        Assert.Equal(0, delta.PendingSnapshotCount(viewer));
    }

    [Fact]
    public void Delta_Forget_ResendsFullStateOnNextCompute()
    {
        var delta = new DeltaCompressor();
        var viewer = new ViewerId("v");

        delta.Compute(viewer, new[] { Entity("a", 1), Entity("b", 1) }, tick: 1);
        delta.Acknowledge(viewer, ackedTick: 1);
        Assert.Empty(delta.Compute(viewer, new[] { Entity("a", 1), Entity("b", 1) }, tick: 2)); // converged

        delta.Forget(viewer); // client reconnected: drop everything we assumed it had

        Assert.Equal(
            new[] { "a", "b" },
            Ids(delta.Compute(viewer, new[] { Entity("a", 1), Entity("b", 1) }, tick: 3)));
    }
}
