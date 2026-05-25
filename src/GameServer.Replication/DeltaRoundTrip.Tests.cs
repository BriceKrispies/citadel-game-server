using Xunit;

namespace GameServer.Replication;

/// <summary>
/// Test #3 (DeltaRoundTrip): the core wire-correctness invariant for prediction/reconciliation.
/// A client that starts from a keyframe and then applies each <see cref="ServerDelta"/>-equivalent
/// frame (changed entities merged, removed entities deleted) on top of its acknowledged baseline
/// must reconstruct EXACTLY the authoritative full snapshot at that tick — across a MULTI-TICK
/// sequence with spawns, version changes, AND despawns/interest churn.
///
/// The oracle is implementation-blind: a <see cref="ClientView"/> holds only what frames delivered.
/// On a keyframe it replaces its view; on an incremental frame it merges changed + applies removed.
/// It never reads engine internals, so the invariant can only be satisfied by genuinely correct
/// delta + removal computation, not by how the baseline happens to be stored.
/// </summary>
public sealed class DeltaRoundTripTests
{
    private static readonly ReplicationPolicy DeltaEveryone =
        ReplicationPolicy.Default with { SnapshotMode = SnapshotMode.Delta };

    private static EntitySnapshot Entity(string id, long version, double x = 0) =>
        new(new EntityId(id), version, new RelevanceKey(x, 0, string.Empty), new[] { (byte)version });

    private static Viewer Watcher(string id, double x = 0) => new(new ViewerId(id), new RelevanceKey(x, 0, string.Empty));

    // The authoritative full (id -> version) map the client must reconstruct each tick.
    private static IReadOnlyDictionary<string, long> Full(IEnumerable<EntitySnapshot> world) =>
        world.ToDictionary(e => e.Id.Value, e => e.Version);

    /// <summary>A client that holds only delivered state — no handle to the engine's baseline.</summary>
    private sealed class ClientView
    {
        private readonly Dictionary<string, long> _view = new();

        public void Apply(ReplicationMessage frame)
        {
            // A keyframe is the complete relevant set: replace. An incremental frame merges the
            // changed entities and deletes the removed ones — exactly what a ServerDelta carries.
            if (frame.IsKeyframe)
            {
                _view.Clear();
            }

            foreach (var entity in frame.Entities)
            {
                _view[entity.Id.Value] = entity.Version;
            }

            foreach (var removed in frame.Removed)
            {
                _view.Remove(removed.Value);
            }
        }

        public IReadOnlyDictionary<string, long> View => _view;
    }

    [Fact]
    public void DeltaAppliedToBaseline_EqualsFullSnapshot_AcrossMultiTickSpawnChangeDespawn()
    {
        var replicator = new Replicator(DeltaEveryone);
        var viewer = Watcher("v");
        var client = new ClientView();

        // A scripted authoritative world per tick: spawns, version bumps, and despawns.
        var worldByTick = new List<EntitySnapshot[]>
        {
            new[] { Entity("a", 1), Entity("b", 1) },                         // t0: spawn a,b
            new[] { Entity("a", 2), Entity("b", 1), Entity("c", 1) },         // t1: a changes, c spawns
            new[] { Entity("a", 2), Entity("c", 2) },                         // t2: b despawns, c changes
            new[] { Entity("a", 2), Entity("c", 2) },                         // t3: nothing changes
            new[] { Entity("a", 3), Entity("c", 2), Entity("d", 1) },         // t4: a changes, d spawns
            Array.Empty<EntitySnapshot>(),                                    // t5: everything despawns
            new[] { Entity("e", 1) },                                         // t6: fresh spawn after empty
        };

        for (var tick = 0; tick < worldByTick.Count; tick++)
        {
            var world = worldByTick[tick];
            var frame = Assert.Single(replicator.Replicate(new[] { viewer }, world, tick));
            client.Apply(frame);

            // The client confirms receipt+apply, advancing the baseline to this tick — so the
            // NEXT frame is a true delta against exactly the state asserted equal here.
            replicator.Acknowledge(viewer.Id, tick);

            Assert.Equal(Full(world), client.View);
        }
    }

    [Fact]
    public void DeltaCarriesRemovals_WhenEntityLeavesView()
    {
        var replicator = new Replicator(DeltaEveryone);
        var viewer = Watcher("v");

        // t0 keyframe: a,b both present.
        var keyframe = Assert.Single(replicator.Replicate(new[] { viewer }, new[] { Entity("a", 1), Entity("b", 1) }, tick: 0));
        Assert.True(keyframe.IsKeyframe);
        Assert.Empty(keyframe.Removed); // a keyframe replaces the whole view; it carries no removals
        replicator.Acknowledge(viewer.Id, 0);

        // t1: b is gone. The incremental frame must name b as removed (and carry no changed entities).
        var delta = Assert.Single(replicator.Replicate(new[] { viewer }, new[] { Entity("a", 1) }, tick: 1));
        Assert.False(delta.IsKeyframe);
        Assert.Empty(delta.Entities);
        Assert.Equal(new[] { "b" }, delta.Removed.Select(r => r.Value).ToArray());
    }

    [Fact]
    public void FirstFrameIsKeyframe_SubsequentAreIncremental()
    {
        var replicator = new Replicator(DeltaEveryone);
        var viewer = Watcher("v");
        var world = new[] { Entity("a", 1) };

        Assert.True(Assert.Single(replicator.Replicate(new[] { viewer }, world, tick: 0)).IsKeyframe);
        replicator.Acknowledge(viewer.Id, 0);
        Assert.False(Assert.Single(replicator.Replicate(new[] { viewer }, world, tick: 1)).IsKeyframe);

        // Reconnect resets the baseline → the next frame is a keyframe again.
        replicator.Resubscribe(viewer.Id);
        Assert.True(Assert.Single(replicator.Replicate(new[] { viewer }, world, tick: 2)).IsKeyframe);
    }
}
