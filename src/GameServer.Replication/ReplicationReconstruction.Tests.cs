using Xunit;

namespace GameServer.Replication;

/// <summary>
/// Reconstruction-invariant tests for the replication engine.
///
/// The oracle is implementation-blind: a <see cref="FakeClient"/> starts empty,
/// applies only the frames it actually receives, and acks only what it applied.
/// The invariant under test is that such a client converges to the authoritative
/// world — under clean delivery, a dropped frame, and a reconnect (total client
/// state loss while the server keeps the viewer's identity).
///
/// These tests never read engine internals (no baseline/pending inspection), so
/// they cannot be satisfied by changing how a baseline is stored — only by the
/// client genuinely being able to rebuild state from delivered frames.
///
/// Scope: additions and version changes only (no entity removals — removal
/// signalling is a separate delta-protocol concern). Interest = Everyone and
/// budget = off, so the delta/reconnect behavior is isolated from those filters.
/// </summary>
public sealed class ReplicationReconstructionTests
{
    private static readonly ReplicationPolicy DeltaEveryone =
        ReplicationPolicy.Default with { SnapshotMode = SnapshotMode.Delta };

    private static EntitySnapshot Entity(string id, long version) =>
        new(new EntityId(id), version, RelevanceKey.None, new byte[4]);

    private static Viewer Watcher(string id) => new(new ViewerId(id), RelevanceKey.None);

    // The authoritative (id, version) set the client should be able to reconstruct.
    private static (string Id, long Version)[] Authoritative(IEnumerable<EntitySnapshot> world) =>
        world.Select(e => (Id: e.Id.Value, Version: e.Version)).OrderBy(t => t.Id).ToArray();

    /// <summary>
    /// A client that holds only what it has received and applied. It has no handle
    /// to the server's baseline; reconstruction is judged purely from delivered frames.
    /// </summary>
    private sealed class FakeClient
    {
        private readonly Dictionary<EntityId, long> _view = new();

        public void Apply(ReplicationMessage message)
        {
            // A full snapshot is the complete relevant set; a delta merges into the view.
            if (message.Mode == SnapshotMode.Full)
            {
                _view.Clear();
            }

            foreach (var entity in message.Entities)
            {
                _view[entity.Id] = entity.Version;
            }
        }

        // Reconnect: the client lost everything it had received.
        public void Reset() => _view.Clear();

        public (string Id, long Version)[] View =>
            _view.Select(kvp => (Id: kvp.Key.Value, Version: kvp.Value)).OrderBy(t => t.Id).ToArray();
    }

    [Fact]
    public void SteadyDelivery_ReconstructsWorld_AndStaysConverged()
    {
        var replicator = new Replicator(DeltaEveryone);
        var viewer = Watcher("v");
        var client = new FakeClient();
        var world = new[] { Entity("a", 1), Entity("b", 1), Entity("c", 1) };

        for (var tick = 0; tick < 5; tick++)
        {
            foreach (var message in replicator.Replicate(new[] { viewer }, world, tick))
            {
                client.Apply(message);
                replicator.Acknowledge(message.Viewer, tick); // delivered + acked
            }

            Assert.Equal(Authoritative(world), client.View);
        }
    }

    [Fact]
    public void DroppedFrame_SelfHeals_WhenNextFrameIsDelivered()
    {
        var replicator = new Replicator(DeltaEveryone);
        var viewer = Watcher("v");
        var client = new FakeClient();
        var world = new[] { Entity("a", 1) };

        // Tick 0: the engine emits the keyframe, but the client drops it (no apply, no ack).
        _ = replicator.Replicate(new[] { viewer }, world, tick: 0);
        Assert.Empty(client.View);

        // Tick 1: unacked → the engine must resend, and this time delivery succeeds.
        foreach (var message in replicator.Replicate(new[] { viewer }, world, tick: 1))
        {
            client.Apply(message);
            replicator.Acknowledge(message.Viewer, ackedTick: 1);
        }

        Assert.Equal(Authoritative(world), client.View);
    }

    [Fact]
    public void Reconnect_WithStaticWorld_ReconstructsState()
    {
        var replicator = new Replicator(DeltaEveryone);
        var viewer = Watcher("v");
        var client = new FakeClient();
        var world = new[] { Entity("a", 1), Entity("b", 1), Entity("c", 1) };

        // Converge normally: the client receives the keyframe and acks it.
        foreach (var message in replicator.Replicate(new[] { viewer }, world, tick: 0))
        {
            client.Apply(message);
            replicator.Acknowledge(message.Viewer, ackedTick: 0);
        }
        Assert.Equal(Authoritative(world), client.View);

        // Reconnect: the client loses all state and re-subscribes under the same
        // ViewerId. Resubscribe is the reconnect signal — without it the engine has
        // no way to know the client no longer holds its acknowledged baseline.
        client.Reset();
        replicator.Resubscribe(viewer.Id);

        // With delivery and acks both flowing, a bounded number of ticks must be
        // enough for the client to hold the full world again.
        for (var tick = 1; tick <= 10; tick++)
        {
            foreach (var message in replicator.Replicate(new[] { viewer }, world, tick))
            {
                client.Apply(message);
                replicator.Acknowledge(message.Viewer, tick);
            }
        }

        Assert.Equal(Authoritative(world), client.View);
    }
}
