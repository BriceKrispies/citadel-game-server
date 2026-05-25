using GameServer.Protocol;
using GameServer.Replication;
using GameServer.Transport.Testing;
using Xunit;

namespace GameServer.Transport;

/// <summary>
/// Server-level reliability tests for ack-driven delta baselines (Delta snapshot
/// mode). A viewer's baseline — the state the server believes the client already
/// holds, and therefore stops resending — must advance only when the client
/// confirms receipt with a <see cref="ClientAck"/>, never because a write to the
/// transport succeeded. A successful send means "queued to the socket", not
/// "received and applied by the client"; conflating the two strands a client that
/// dropped/never-applied a frame, because a static world then produces empty deltas
/// forever.
///
/// These assert only externally visible outcomes (the entities in the snapshots the
/// client receives), never engine internals, so they cannot be satisfied by
/// reshuffling how the baseline is stored.
///
/// The two tests are deliberately discriminating:
///   • <see cref="UnackedSnapshot_IsResentOnNextTick"/> fails for the current
///     "ack on send-success" wiring (and for any fix that keeps it).
///   • <see cref="AfterClientAck_UnchangedStateIsNotResent"/> fails for a degenerate
///     "never advance the baseline" fix (which would resend everything every tick).
/// Only advancing the baseline on a real ClientAck satisfies both.
/// </summary>
public sealed class RealtimeServerDeltaAckTests
{
    private static readonly RoomId Arena = new("arena");
    private static readonly PlayerId Player = new("p1");

    // A server whose game policy uses delta snapshots, reusing the harness's fakes.
    // Full mode never keeps a per-viewer baseline, so the send-vs-ack distinction is
    // only observable under Delta.
    private static RealtimeServer DeltaServer(SliceHarness harness) =>
        new(harness.Tenants, harness.Router, harness.Snapshots, harness.Events, harness.Telemetry,
            _ => ReplicationPolicy.Default with { SnapshotMode = SnapshotMode.Delta });

    private static bool ContainsPlayer(ServerSnapshot snapshot) =>
        snapshot.Entities.Any(e => e.EntityId == Player.Value);

    private static ServerSnapshot SoleSnapshot(FakeClient client) =>
        client.Received().Select(m => m.Payload).OfType<ServerSnapshot>().Single();

    // The latest authoritative-state message on the wire, snapshot OR delta, and whether it
    // carried the player's entity. After the viewer holds an acked baseline, the wire sends a
    // ServerDelta (not a ServerSnapshot) carrying only what changed; a baseline-less viewer gets
    // a full ServerSnapshot keyframe.
    private static bool LatestStateContainsPlayer(FakeClient client)
    {
        var last = client.Received()
            .Select(m => m.Payload)
            .Last(p => p is ServerSnapshot or ServerDelta);
        return last switch
        {
            ServerSnapshot s => s.Entities.Any(e => e.EntityId == Player.Value),
            ServerDelta d => d.Changed.Any(e => e.EntityId == Player.Value),
            _ => false,
        };
    }

    [Fact]
    public async Task UnackedSnapshot_IsResentOnNextTick()
    {
        var harness = new SliceHarness("tenant-a");
        var server = DeltaServer(harness);
        var (transport, client) = harness.NewClient("c1", "tenant-a", "p1");
        var key = harness.Key("tenant-a", "arena");

        client.Hello();
        client.Join(Arena);
        client.Close();
        await server.HandleConnectionAsync(transport, client.Principal);

        // Tick 1: the keyframe (a full ServerSnapshot, since the viewer has no baseline). The
        // write succeeds (the in-memory transport always accepts), but the client sends no ClientAck.
        await server.TickRoom(key);
        Assert.True(ContainsPlayer(SoleSnapshot(client)), "first tick should carry the player (keyframe)");

        // Tick 2: still no ack and the world is unchanged. Because the client never confirmed
        // receipt, the viewer still holds no baseline, so the server resends a full keyframe rather
        // than assume the first landed.
        await server.TickRoom(key);
        Assert.True(LatestStateContainsPlayer(client), "unacked state must be resent until the client acks");
    }

    [Fact]
    public async Task AfterClientAck_UnchangedStateIsNotResent()
    {
        var harness = new SliceHarness("tenant-a");
        var server = DeltaServer(harness);
        var (transport, client) = harness.NewClient("c1", "tenant-a", "p1");
        var key = harness.Key("tenant-a", "arena");

        // Drive the connection in stages so an ack can be processed *between* ticks.
        // HandleConnectionAsync drains all buffered inbound synchronously, then parks
        // awaiting more, so by the time it returns its (pending) task the hello+join
        // are fully processed.
        client.Hello();
        client.Join(Arena);
        var loop = server.HandleConnectionAsync(transport, client.Principal);

        // Tick 1: keyframe (ServerSnapshot) carrying the player.
        await server.TickRoom(key);
        Assert.True(ContainsPlayer(SoleSnapshot(client)), "first tick should carry the player (keyframe)");

        // The client confirms it received and applied tick 1, then closes. Awaiting
        // the loop guarantees the ack is processed before we continue (the channel
        // delivers the buffered ack before reporting completion).
        client.Ack(ackedServerTick: 1, room: Arena);
        client.Close();
        await loop;

        // Tick 2: the world is unchanged and the client has acked the only state it holds, so the
        // ServerDelta carries nothing new. A degenerate "never advance the baseline" fix would
        // resend the player here and fail this assertion.
        await server.TickRoom(key);
        Assert.False(LatestStateContainsPlayer(client), "acked, unchanged state must not be resent");
    }

    [Fact]
    public async Task DeltaBaseline_PersistsAcrossMultipleTicks_ReplicatorIsNotRecreated()
    {
        // Guards the join-vs-tick replicator-creation seam: the per-room replicator (and
        // thus the per-viewer delta baseline) is established once at join with the room's
        // policy. If TickRoom were ever able to recreate it — e.g. with the conservative
        // default policy — the acked baseline would be lost and the player would reappear.
        var harness = new SliceHarness("tenant-a");
        var server = DeltaServer(harness);
        var (transport, client) = harness.NewClient("c1", "tenant-a", "p1");
        var key = harness.Key("tenant-a", "arena");

        client.Hello();
        client.Join(Arena);
        var loop = server.HandleConnectionAsync(transport, client.Principal);

        await server.TickRoom(key); // tick 1: keyframe carrying the player
        client.Ack(ackedServerTick: 1, room: Arena);
        client.Close();
        await loop;

        // Several more unchanged, unacked ticks. The baseline must survive all of them.
        await server.TickRoom(key);
        await server.TickRoom(key);

        // The single keyframe (tick 1, before the baseline existed) carries the player; every
        // post-ack tick is a ServerDelta, and none may resend the persisted baseline.
        // (Drain once: Received() consumes the outbound buffer.)
        var received = client.Received().Select(m => m.Payload).ToList();
        var snapshots = received.OfType<ServerSnapshot>().ToList();
        var deltas = received.OfType<ServerDelta>().ToList();
        Assert.True(ContainsPlayer(Assert.Single(snapshots)), "first tick should carry the player (keyframe)");
        Assert.NotEmpty(deltas);
        Assert.All(deltas, d => Assert.DoesNotContain(d.Changed, e => e.EntityId == Player.Value));
    }
}
