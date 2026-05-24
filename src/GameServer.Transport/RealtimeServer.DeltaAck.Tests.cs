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
        snapshot.Players.Any(p => p.PlayerId == Player);

    private static ServerSnapshot SoleSnapshot(FakeClient client) =>
        client.Received().Select(m => m.Payload).OfType<ServerSnapshot>().Single();

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

        // Tick 1: the keyframe. The write succeeds (the in-memory transport always
        // accepts), but the client sends no ClientAck.
        await server.TickRoom(key);
        Assert.True(ContainsPlayer(SoleSnapshot(client)), "first tick should carry the player (keyframe)");

        // Tick 2: still no ack and the world is unchanged. Because the client never
        // confirmed receipt, the server must resend the player's state rather than
        // assume it landed. RED today: send-success was treated as an ack, so the
        // baseline advanced and this snapshot is empty.
        await server.TickRoom(key);
        Assert.True(ContainsPlayer(SoleSnapshot(client)), "unacked state must be resent until the client acks");
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

        // Tick 1: keyframe carrying the player.
        await server.TickRoom(key);
        Assert.True(ContainsPlayer(SoleSnapshot(client)), "first tick should carry the player (keyframe)");

        // The client confirms it received and applied tick 1, then closes. Awaiting
        // the loop guarantees the ack is processed before we continue (the channel
        // delivers the buffered ack before reporting completion).
        client.Ack(ackedServerTick: 1, room: Arena);
        client.Close();
        await loop;

        // Tick 2: the world is unchanged and the client has acked the only state it
        // holds, so there is nothing new to send. A degenerate "never advance the
        // baseline" fix would resend the player here and fail this assertion.
        await server.TickRoom(key);
        Assert.False(ContainsPlayer(SoleSnapshot(client)), "acked, unchanged state must not be resent");
    }
}
