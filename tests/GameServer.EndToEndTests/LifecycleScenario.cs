using System.Net.Http.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using GameServer.Protocol.Realtime.V1;
using GameServer.SecurityTests; // link-compiled harness: SecurityTarget, RealtimeWireClient, WireEnvelopes, ControlPlane
using GameServer.Simulation;    // MoveRightGame.MoveRight / DecodeX
using Xunit.Abstractions;

namespace GameServer.EndToEndTests;

/// <summary>
/// The full multiplayer lifecycle, end to end, against the REAL hardened container treated as a black
/// box. Testcontainers starts the prebuilt <c>citadel-host:local</c> image (built by
/// <c>scripts/podman-build.ps1</c>), maps its :8080 to a random host port (so it sidesteps the
/// Windows+Podman published-port relay), and the test drives it purely over HTTP + WebSocket:
/// <list type="number">
///   <item>start the container (black box); the built-in <c>demo-game</c> is ready;</item>
///   <item>an authed client lists the available rooms for the game — there is always ≥1, with state;</item>
///   <item>client #1 connects to that room and sees the current game state;</item>
///   <item>client #2 joins the SAME room;</item>
///   <item>client #1 submits an intent (<c>MoveRight</c>);</item>
///   <item>the server reconciles on its tick and broadcasts — BOTH clients observe player x == 1.</item>
/// </list>
/// With no container engine (or no built image) it SKIPS (not fails), like the other container scenarios.
/// </summary>
public sealed class LifecycleScenario
{
    private const string Image = "citadel-host:local";
    private const string Game = "demo-game";

    // Injected into the container at runtime (NOT baked into the image); >=32 chars satisfies fail-closed.
    private const string JoinSecret = "citadel-e2e-join-secret-0123456789-abcdef";
    private const string TenantAKey = "e2e-tenant-a-key-0001";
    private const string TenantBKey = "e2e-tenant-b-key-0002";
    private const string AdminKey = "e2e-platform-admin-key-0003";

    private readonly ITestOutputHelper _output;

    public LifecycleScenario(ITestOutputHelper output) => _output = output;

    private sealed record RoomSummary(
        string RoomId, string GameId, string TenantId, string Status, int SubscriberCount, long Tick, bool Live);
    private sealed record GameRooms(string GameId, IReadOnlyList<RoomSummary> Rooms);

    private static async Task<IContainer> StartHostAsync()
    {
        try
        {
            var container = new ContainerBuilder(Image)
                .WithEnvironment("Auth__JoinTokenSecret", JoinSecret)
                .WithEnvironment("ControlPlane__ApiKeys__0__Key", TenantAKey)
                .WithEnvironment("ControlPlane__ApiKeys__0__CallerId", "e2e-operator-a")
                .WithEnvironment("ControlPlane__ApiKeys__0__TenantId", "tenant-a")
                .WithEnvironment("ControlPlane__ApiKeys__1__Key", TenantBKey)
                .WithEnvironment("ControlPlane__ApiKeys__1__CallerId", "e2e-operator-b")
                .WithEnvironment("ControlPlane__ApiKeys__1__TenantId", "tenant-b")
                .WithEnvironment("ControlPlane__ApiKeys__2__Key", AdminKey)
                .WithEnvironment("ControlPlane__ApiKeys__2__CallerId", "e2e-admin")
                .WithEnvironment("ControlPlane__ApiKeys__2__TenantId", "tenant-a")
                .WithEnvironment("ControlPlane__ApiKeys__2__Roles__0", "platform-admin")
                .WithPortBinding(8080, assignRandomHostPort: true)
                .WithWaitStrategy(Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/ready")))
                .Build();
            await container.StartAsync();
            return container;
        }
        catch (Exception ex)
        {
            // No container engine reachable, or the image isn't built -> skip (not fail), exactly like
            // RedisRoomDirectoryScenario / PostgresPersistenceScenario. Build it with scripts/podman-build.ps1.
            throw new SkipException(
                $"Container engine / image '{Image}' not available ({ex.GetType().Name}: {ex.Message}). " +
                "Build the image first: pwsh scripts/podman-build.ps1");
        }
    }

    [SkippableFact]
    public async Task DemoGame_FullLifecycle_BothClientsObserveTheMove()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = cts.Token;

        // STEP 1: start the hardened container as a black box (demo-game is built in / ready).
        await using var host = await StartHostAsync();
        var baseUrl = $"http://{host.Hostname}:{host.GetMappedPublicPort(8080)}";
        var target = SecurityTarget.ForExplicit(baseUrl, TenantAKey, TenantBKey, AdminKey, JoinSecret);
        _output.WriteLine($"container ready at {baseUrl}");

        // STEP 2: an authed client lists the available rooms for the game; there is always >=1, with state.
        var rooms = await ListGameRoomsAsync(target, TenantAKey, Game, ct);
        Assert.NotEmpty(rooms);
        var roomId = rooms[0].RoomId;
        _output.WriteLine($"rooms for {Game}: {string.Join(", ", rooms.Select(r => $"{r.RoomId}(live={r.Live},tick={r.Tick},subs={r.SubscriberCount})"))}");

        // STEP 3: client #1 connects to that room (mints a token, opens the WS) and sees current state.
        const string p1 = "p1";
        await using var c1 = await RealtimeWireClient.ConnectWithMintedTokenAsync(target, TenantAKey, roomId, p1, ct);
        await c1.SendAsync(WireEnvelopes.Hello(SecurityTarget.TenantA, Game), ct);
        await c1.SendAsync(WireEnvelopes.JoinRoom(SecurityTarget.TenantA, Game, roomId, p1), ct);
        Assert.True((await c1.ReadUntilRejectedSnapshotOrCloseAsync(ct)).AccessWasGranted,
            "client #1 should have joined and received a snapshot of the current state.");

        // STEP 4: client #2 joins the SAME room.
        const string p2 = "p2";
        await using var c2 = await RealtimeWireClient.ConnectWithMintedTokenAsync(target, TenantAKey, roomId, p2, ct);
        await c2.SendAsync(WireEnvelopes.Hello(SecurityTarget.TenantA, Game), ct);
        await c2.SendAsync(WireEnvelopes.JoinRoom(SecurityTarget.TenantA, Game, roomId, p2), ct);
        Assert.True((await c2.ReadUntilRejectedSnapshotOrCloseAsync(ct)).AccessWasGranted,
            "client #2 should have joined the same room.");

        // STEP 5: client #1 submits an intent to update state.
        await c1.SendAsync(
            WireEnvelopes.Command(SecurityTarget.TenantA, Game, roomId, p1, MoveRightGame.MoveRight, sequence: 1), ct);

        // STEP 6: the server reconciles on its tick and broadcasts — BOTH clients observe player p1 at x == 1.
        var x1 = await ReadXForPlayerAsync(c1, p1, expected: 1, ct);
        var x2 = await ReadXForPlayerAsync(c2, p1, expected: 1, ct);
        _output.WriteLine($"after MoveRight: client#1 sees p1.x={x1}, client#2 sees p1.x={x2}");
        Assert.Equal(1, x1);
        Assert.Equal(1, x2);
    }

    /// <summary>GET /api/v1/games/{game}/rooms with the tenant key; returns the available rooms.</summary>
    private static async Task<IReadOnlyList<RoomSummary>> ListGameRoomsAsync(
        SecurityTarget target, string apiKey, string game, CancellationToken ct)
    {
        using var client = target.NewHttpClient(apiKey);
        var resp = await client.GetAsync($"/api/v1/games/{game}/rooms", ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<GameRooms>(ct);
        return body?.Rooms ?? throw new InvalidOperationException("Empty room-listing body.");
    }

    /// <summary>
    /// Reads server frames until the snapshot/delta that carries <paramref name="playerId"/> with
    /// x ≥ <paramref name="expected"/>, then returns that x. Read-until-condition (bounded by the
    /// caller's CTS) so the 10 Hz tick cadence can't make it flaky — no sleeps, no tick-count math.
    /// MoveRightGame projects the player id as the entity id and encodes x as little-endian int32.
    /// </summary>
    private static async Task<int> ReadXForPlayerAsync(
        RealtimeWireClient client, string playerId, int expected, CancellationToken ct)
    {
        while (true)
        {
            var env = await client.ReceiveEnvelopeAsync(ct);
            Skip.If(env is null, "Connection closed before the expected state arrived.");

            var entity = env!.PayloadCase switch
            {
                RealtimeEnvelope.PayloadOneofCase.ServerSnapshot =>
                    env.ServerSnapshot.Entities.FirstOrDefault(e => e.EntityId == playerId),
                RealtimeEnvelope.PayloadOneofCase.ServerDelta =>
                    env.ServerDelta.ChangedEntities.FirstOrDefault(e => e.EntityId == playerId),
                _ => null,
            };
            if (entity is null)
            {
                continue;
            }

            var x = MoveRightGame.DecodeX(entity.Payload.ToByteArray());
            if (x >= expected)
            {
                return x;
            }
        }
    }
}
