using GameServer.Observability;
using GameServer.Protocol;
using GameServer.Simulation;
using GameServer.Tenancy;
using GameServer.Transport.Testing;
using Xunit;

namespace GameServer.Transport;

/// <summary>
/// Wave 1 hard safety boundaries, proven on the realtime hot path: a kernel-level inbound
/// command-size bound (#7), per-tenant rate limiting (Gap A), and per-tenant inbound attribution
/// (#9). These pin that the controls are ENFORCED in <c>HandleCommandAsync</c> /
/// <c>HandleConnectionAsync</c>, not merely built and dormant.
/// </summary>
public sealed class RealtimeServerSafetyTests
{
    private static readonly RoomId Arena = new("arena");

    private static int X(IGameRoom room, string player) =>
        MoveRightGame.DecodeX(room.Project().Single(e => e.Id.Value == player).Payload);

    // #7 — message-size limit (unit): an oversized command is rejected with a typed error and the
    // connection SURVIVES, so a following well-formed command is still accepted and applied.
    [Fact]
    public async Task OversizedCommand_IsRejected_AndConnectionSurvives()
    {
        var harness = new SliceHarness(
            maxQueueDepth: GameRoom.DefaultMaxQueueDepth,
            rateLimiter: null,
            tenantMetrics: null,
            maxCommandBytes: 64,
            "tenant-a");
        var (transport, client) = harness.NewClient("c1", "tenant-a", "p1");

        var oversized = new string('x', 65); // one byte past the 64-byte bound

        client.Hello();
        client.Join(Arena);
        client.Command(oversized, Arena);                 // rejected by the size guard
        client.Command(MoveRightGame.MoveRight, Arena);   // connection survived -> still processed
        client.Close();
        await harness.Server.HandleConnectionAsync(transport, client.Principal);

        // The oversized frame produced exactly one typed rejection (no disconnect, no crash).
        var error = client.Received().Select(m => m.Payload).OfType<ServerError>().Single();
        Assert.Equal(ServerErrorCode.MalformedMessage, error.Code);

        // The following valid command was still accepted and applied — the connection lived on.
        var key = harness.Key("tenant-a", "arena");
        await harness.Server.TickRoom(key);
        Assert.True(harness.Router.TryGetRoom(key, out var room));
        Assert.Equal(1, X(room, "p1"));
    }

    [Fact]
    public async Task CommandAtTheSizeLimit_IsAccepted()
    {
        // Boundary: a command exactly at the limit is admitted (the guard rejects only when OVER).
        var harness = new SliceHarness(
            maxQueueDepth: GameRoom.DefaultMaxQueueDepth,
            rateLimiter: null,
            tenantMetrics: null,
            maxCommandBytes: MoveRightGame.MoveRight.Length,
            "tenant-a");
        var (transport, client) = harness.NewClient("c1", "tenant-a", "p1");

        client.Hello();
        client.Join(Arena);
        client.Command(MoveRightGame.MoveRight, Arena);
        client.Close();
        await harness.Server.HandleConnectionAsync(transport, client.Principal);

        Assert.Empty(client.Received().Select(m => m.Payload).OfType<ServerError>());
    }

    // Gap A (unit) — a sustained single-tenant flood is shed by the per-tenant token bucket as a
    // retryable Overloaded error, the wire's BACKPRESSURE_REJECTED. This is what makes the limiter
    // observable on the hot path (it was dormant before).
    [Fact]
    public async Task SustainedTenantFlood_IsRateLimited_OnTheHotPath()
    {
        var clock = new FakeMonotonicClock();
        // Tiny bucket so the flood overruns it within the burst: burst of 3, no refill (clock frozen).
        var limiter = new TokenBucketTenantRateLimiter(permitsPerSecond: 1, burst: 3, clock);
        var harness = new SliceHarness(
            maxQueueDepth: GameRoom.DefaultMaxQueueDepth,
            rateLimiter: limiter,
            tenantMetrics: null,
            maxCommandBytes: 0,
            "tenant-a");
        var (transport, client) = harness.NewClient("c1", "tenant-a", "p1");

        client.Hello();
        client.Join(Arena);
        for (var i = 0; i < 10; i++)
        {
            client.Command(MoveRightGame.MoveRight, Arena, sequence: i + 1);
        }

        client.Close();
        await harness.Server.HandleConnectionAsync(transport, client.Principal);

        var errors = client.Received().Select(m => m.Payload).OfType<ServerError>().ToList();
        // Burst (3) admitted; the remaining 7 shed as Overloaded.
        Assert.Equal(7, errors.Count);
        Assert.All(errors, e => Assert.Equal(ServerErrorCode.Overloaded, e.Code));
        Assert.Equal(7, harness.Telemetry.CountIncrements(TelemetryMetrics.BackpressureRejections));
    }

    [Fact]
    public async Task RateLimiter_DoesNotThrottle_AQuietTenant()
    {
        // Isolation: one tenant draining its bucket must not touch another tenant's allowance.
        var clock = new FakeMonotonicClock();
        var limiter = new TokenBucketTenantRateLimiter(permitsPerSecond: 1, burst: 3, clock);
        var harness = new SliceHarness(
            maxQueueDepth: GameRoom.DefaultMaxQueueDepth,
            rateLimiter: limiter,
            tenantMetrics: null,
            maxCommandBytes: 0,
            "tenant-a", "tenant-b");

        // tenant-a floods and drains its bucket.
        var (ta, ca) = harness.NewClient("c-a", "tenant-a", "p1");
        ca.Hello();
        ca.Join(Arena);
        for (var i = 0; i < 10; i++)
        {
            ca.Command(MoveRightGame.MoveRight, Arena, sequence: i + 1);
        }

        ca.Close();
        await harness.Server.HandleConnectionAsync(ta, ca.Principal);

        // tenant-b sends a polite handful — none should be throttled.
        var (tb, cb) = harness.NewClient("c-b", "tenant-b", "p1");
        cb.Hello();
        cb.Join(Arena);
        for (var i = 0; i < 3; i++)
        {
            cb.Command(MoveRightGame.MoveRight, Arena, sequence: i + 1);
        }

        cb.Close();
        await harness.Server.HandleConnectionAsync(tb, cb.Principal);

        Assert.Empty(cb.Received().Select(m => m.Payload).OfType<ServerError>());
    }

    // #9 — per-tenant inbound attribution: every inbound message is counted against the sender's
    // tenant at the edge, so the noisy tenant is rankable where the global sink cannot attribute.
    [Fact]
    public async Task InboundMessages_AreAttributed_PerTenant_AtTheEdge()
    {
        var perTenant = new TenantScopedMetrics();
        var harness = new SliceHarness(
            maxQueueDepth: GameRoom.DefaultMaxQueueDepth,
            rateLimiter: null,
            tenantMetrics: perTenant,
            maxCommandBytes: 0,
            "tenant-a", "tenant-b");

        // tenant-a: hello + join + 5 commands = 7 inbound messages.
        var (ta, ca) = harness.NewClient("c-a", "tenant-a", "p1");
        ca.Hello();
        ca.Join(Arena);
        for (var i = 0; i < 5; i++)
        {
            ca.Command(MoveRightGame.MoveRight, Arena, sequence: i + 1);
        }

        ca.Close();
        await harness.Server.HandleConnectionAsync(ta, ca.Principal);

        // tenant-b: hello + join only = 2 inbound messages.
        var (tb, cb) = harness.NewClient("c-b", "tenant-b", "p1");
        cb.Hello();
        cb.Join(Arena);
        cb.Close();
        await harness.Server.HandleConnectionAsync(tb, cb.Principal);

        var ranked = perTenant.Ranked(TelemetryMetrics.MessagesIn, topN: 2);
        Assert.Equal("tenant-a", ranked[0].Tenant); // the noisy tenant surfaces first
        Assert.Equal(7, ranked[0].Total);
        Assert.Equal(2, ranked.Single(s => s.Tenant == "tenant-b").Total);
    }
}
