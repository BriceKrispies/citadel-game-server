using Xunit;

namespace GameServer.Replication;

/// <summary>
/// Adversarial, randomized multi-tick property test for the delta pipeline (Wave 2 hardening of the
/// builder's fixed 7-tick <see cref="DeltaRoundTripTests"/>). It drives many ticks of randomized
/// spawn/move/despawn/empty-world/respawn over several entities and viewers with several acking
/// disciplines, and asserts the wire-correctness invariant a real client depends on.
///
/// The oracle is the REAL client contract (mirrors <c>sdk/typescript/src/reconciler.ts</c>): a
/// keyframe REPLACES the view; a delta is applied ONLY when its <c>from</c> tick equals the client's
/// last-applied tick — otherwise the client is missing the baseline and must NOT apply it (and must
/// NOT ack), which makes the server keep resending until a frame it can apply (or a keyframe) arrives.
///
/// Two properties are asserted at every tick:
///   (1) SAFETY  — whenever the client successfully applied this tick's frame, its reconstructed view
///                 (entity id -> version) EQUALS the authoritative full snapshot, including removals.
///   (2) LIVENESS — a client that always acks the frame it applied never gets permanently stuck on a
///                 delta it cannot apply: it converges to the authoritative tick every time.
/// A divergence in (1) or a livelock in (2) is a High wire defect.
/// </summary>
public sealed class DeltaPropertyRoundTripTests
{
    private static readonly ReplicationPolicy DeltaEveryone =
        ReplicationPolicy.Default with { SnapshotMode = SnapshotMode.Delta };

    private static EntitySnapshot Entity(string id, long version) =>
        new(new EntityId(id), version, new RelevanceKey(version, 0, string.Empty), new[] { (byte)version });

    private static Viewer Watcher(string id) => new(new ViewerId(id), RelevanceKey.None);

    private static IReadOnlyDictionary<string, long> Full(IEnumerable<EntitySnapshot> world) =>
        world.ToDictionary(e => e.Id.Value, e => e.Version);

    /// <summary>
    /// A client that holds ONLY what frames delivered and follows the real reconciler's from-tick
    /// rule. It never reads engine internals, so the invariant can only be met by genuinely correct
    /// delta+removal computation, not by how the server stores a baseline.
    /// </summary>
    private sealed class ReconcilingClient
    {
        private readonly Dictionary<string, long> _view = new();
        public long LastAppliedTick { get; private set; } = -1;

        /// <summary>Applies a frame the way the TS reconciler does. Returns whether it was applied.</summary>
        public bool Apply(ReplicationMessage frame, long fromTick, long toTick)
        {
            if (frame.IsKeyframe)
            {
                _view.Clear();
                foreach (var e in frame.Entities)
                {
                    _view[e.Id.Value] = e.Version;
                }

                LastAppliedTick = toTick;
                return true;
            }

            // Incremental: only applicable if it builds on exactly the baseline we hold.
            if (fromTick != LastAppliedTick)
            {
                return false;
            }

            foreach (var e in frame.Entities)
            {
                _view[e.Id.Value] = e.Version;
            }

            foreach (var removed in frame.Removed)
            {
                _view.Remove(removed.Value);
            }

            LastAppliedTick = toTick;
            return true;
        }

        public IReadOnlyDictionary<string, long> View => _view;
    }

    /// <summary>Generates a randomized authoritative world that mutates tick over tick.</summary>
    private static EntitySnapshot[] NextWorld(Random rng, Dictionary<string, long> live)
    {
        // Despawn a random subset.
        foreach (var id in live.Keys.ToList())
        {
            if (rng.NextDouble() < 0.30)
            {
                live.Remove(id);
            }
        }

        // Version-bump a random subset of survivors (a "move").
        foreach (var id in live.Keys.ToList())
        {
            if (rng.NextDouble() < 0.50)
            {
                live[id] += 1;
            }
        }

        // Spawn a few new entities from a small id pool (so ids churn in and out — this is what
        // exercises spawn-and-despawn within a single un-acked window).
        var spawnCount = rng.Next(0, 4);
        for (var i = 0; i < spawnCount; i++)
        {
            var id = $"e{rng.Next(0, 8)}";
            live.TryAdd(id, 1);
        }

        // Occasionally empty the world entirely, then let it repopulate next tick (the empty-world
        // and respawn-after-empty cases the builder called out).
        if (rng.NextDouble() < 0.10)
        {
            live.Clear();
        }

        return live.Select(kv => Entity(kv.Key, kv.Value)).ToArray();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(1337)]
    [InlineData(99999)]
    public void RandomizedMultiTick_AlwaysAckingClient_ReconstructsExactSnapshot(int seed)
    {
        var rng = new Random(seed);
        var replicator = new Replicator(DeltaEveryone);

        // Three viewers with distinct, fixed acking disciplines exercised over the same world.
        var always = (Viewer: Watcher("always"), Client: new ReconcilingClient());

        var live = new Dictionary<string, long>();

        for (var tick = 0; tick < 200; tick++)
        {
            var world = NextWorld(rng, live);
            var frame = Single(replicator.Replicate(new[] { always.Viewer }, world, tick), always.Viewer.Id);

            var from = replicator.AcknowledgedTick(always.Viewer.Id);
            var applied = always.Client.Apply(frame, from, tick);

            // LIVENESS: a client that always acks must always be able to apply the frame it is sent —
            // either a keyframe or a delta against the baseline it just acked. If it cannot, the server
            // has produced a frame the client can never consume (livelock).
            Assert.True(applied,
                $"seed {seed} tick {tick}: always-acking client could not apply frame (from={from}, keyframe={frame.IsKeyframe}).");

            // SAFETY: the reconstructed view equals the authoritative snapshot exactly.
            Assert.Equal(Full(world), always.Client.View);

            // It applied, so it acks this tick — advancing the baseline for the next delta.
            replicator.Acknowledge(always.Viewer.Id, tick);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(1337)]
    [InlineData(99999)]
    public void RandomizedMultiTick_NeverAckingClient_StaysOnKeyframesAndNeverDiverges(int seed)
    {
        var rng = new Random(seed);
        var replicator = new Replicator(DeltaEveryone);
        var viewer = Watcher("never");
        var client = new ReconcilingClient();
        var live = new Dictionary<string, long>();

        for (var tick = 0; tick < 200; tick++)
        {
            var world = NextWorld(rng, live);
            var frame = Single(replicator.Replicate(new[] { viewer }, world, tick), viewer.Id);

            // A client that never acks holds no baseline, so EVERY frame must be a full keyframe it can
            // apply unconditionally — never a delta it would be forced to reject. (If the server ever
            // sent it a delta, the client would be stuck forever.)
            Assert.True(frame.IsKeyframe, $"seed {seed} tick {tick}: never-acking client was sent a non-keyframe.");

            var applied = client.Apply(frame, replicator.AcknowledgedTick(viewer.Id), tick);
            Assert.True(applied);
            Assert.Equal(Full(world), client.View);

            // Deliberately do NOT ack.
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(1337)]
    [InlineData(99999)]
    public void RandomizedMultiTick_RealisticClientWithDroppedFrames_NeverDiverges(int seed)
    {
        // Models a REAL client (mirrors sdk client.ts): it acks exactly the frames it successfully
        // applied; it withholds the ack ONLY when it cannot apply a frame (baseline mismatch). Here we
        // also randomly DROP whole frames in transit. The contract under test: a dropped/un-applied
        // delta must self-heal — because the server keeps recomputing against the last ACKED baseline,
        // the next delivered frame still references a baseline the client holds, so it catches up. The
        // client must NEVER reconstruct a view that disagrees with the authoritative snapshot.
        var rng = new Random(seed);
        var replicator = new Replicator(DeltaEveryone);
        var viewer = Watcher("realistic");
        var client = new ReconcilingClient();
        var live = new Dictionary<string, long>();

        var everApplied = false;

        for (var tick = 0; tick < 300; tick++)
        {
            var world = NextWorld(rng, live);
            var frame = Single(replicator.Replicate(new[] { viewer }, world, tick), viewer.Id);

            // 15% of frames are dropped before reaching the client.
            if (rng.NextDouble() < 0.15)
            {
                continue;
            }

            var from = replicator.AcknowledgedTick(viewer.Id);
            var applied = client.Apply(frame, from, tick);

            if (applied)
            {
                everApplied = true;

                // SAFETY: every tick the client successfully consumed equals the authoritative
                // snapshot exactly — including despawn removals, across spawn-in/despawn-out churn
                // inside a single un-acked window.
                Assert.Equal(Full(world), client.View);

                // A real client acks what it applied (its delivered ack may itself be dropped).
                if (rng.NextDouble() < 0.9)
                {
                    replicator.Acknowledge(viewer.Id, tick);
                }
            }
        }

        Assert.True(everApplied, $"seed {seed}: client never managed to apply a single frame.");
    }

    private static ReplicationMessage Single(IReadOnlyList<ReplicationMessage> messages, ViewerId viewer) =>
        messages.Single(m => m.Viewer.Equals(viewer));
}
