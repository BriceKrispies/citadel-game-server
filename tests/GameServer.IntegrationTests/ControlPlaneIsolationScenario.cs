using System.Net;
using System.Net.Http.Json;
using GameServer.ControlPlane;
using GameServer.Identity;
using GameServer.Protocol;
using GameServer.Routing;
using GameServer.Transport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// Wave 7 adversarial focus: the named tests <c>NoCrossTenantEnumeration</c> and
/// <c>RoomTerminateGoesThroughAuthoritativePath</c>, plus per-role least-privilege and role-escalation
/// checks. Treats every control-plane endpoint as attacker-reachable: a tenant caller must never list,
/// read, or terminate another tenant's resources, and a lesser role must never perform a higher op.
/// </summary>
public sealed class ControlPlaneIsolationScenario
{
    private readonly ITestOutputHelper _output;

    public ControlPlaneIsolationScenario(ITestOutputHelper output) => _output = output;

    private const string PlatformAdmin = "dev-admin-key";    // platform-admin, tenant-a
    private const string GameAdminA = "dev-gameadmin-a-key"; // game-admin, tenant-a
    private const string GameAdminB = "dev-gameadmin-b-key"; // game-admin, tenant-b
    private const string OperatorA = "dev-operator-a-key";   // read-only operator, tenant-a

    private static HttpClient Client(WebApplicationFactory<Program> host, string? key)
    {
        var client = host.CreateClient();
        if (key is not null)
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", key);
        }

        return client;
    }

    [Fact]
    public async Task NoCrossTenantEnumeration_ViaListOrGet_OrTerminate()
    {
        using var host = new WebApplicationFactory<Program>();

        // tenant-a's game-admin lists tenants: sees ONLY tenant-a, never tenant-b (no enumeration via list).
        var listA = await Client(host, GameAdminA).GetAsync("/api/v1/tenants");
        Assert.Equal(HttpStatusCode.OK, listA.StatusCode);
        var tenantsA = await listA.Content.ReadFromJsonAsync<List<TenantRecordDto>>();
        Assert.All(tenantsA!, t => Assert.Equal("tenant-a", t.TenantId));
        Assert.DoesNotContain(tenantsA!, t => t.TenantId == "tenant-b");

        // A platform admin sees both — confirms the list is not simply empty.
        var listAdmin = await Client(host, PlatformAdmin).GetAsync("/api/v1/tenants");
        var tenantsAdmin = await listAdmin.Content.ReadFromJsonAsync<List<TenantRecordDto>>();
        Assert.Contains(tenantsAdmin!, t => t.TenantId == "tenant-a");
        Assert.Contains(tenantsAdmin!, t => t.TenantId == "tenant-b");

        // tenant-a's caller reads tenant-b directly: 403 (authorize BEFORE lookup → no existence oracle),
        // and the SAME 403 for a tenant that does not exist, so a caller cannot distinguish existence.
        var readOther = await Client(host, GameAdminA).GetAsync("/api/v1/tenants/tenant-b");
        Assert.Equal(HttpStatusCode.Forbidden, readOther.StatusCode);
        var readGhost = await Client(host, GameAdminA).GetAsync("/api/v1/tenants/tenant-ghost");
        Assert.Equal(HttpStatusCode.Forbidden, readGhost.StatusCode);

        // tenant-a's caller terminates a tenant-b room: 403, NOT a 404 — it cannot probe whether the room
        // exists in another tenant (authorize before touching the room).
        var terminateOther = await Client(host, GameAdminA).PostAsync("/api/v1/admin/rooms/tenant-b/some-room/terminate", content: null);
        Assert.Equal(HttpStatusCode.Forbidden, terminateOther.StatusCode);

        // tenant-a's caller observes a tenant-b room: same 403.
        var observeOther = await Client(host, GameAdminA).GetAsync("/api/v1/admin/rooms/tenant-b/some-room");
        Assert.Equal(HttpStatusCode.Forbidden, observeOther.StatusCode);

        // tenant-a's caller REPLAYS a tenant-b room from durable artifacts: 403, NOT a 404 — authorize
        // before reading any of tenant-b's snapshot/event data, so replay cannot be a cross-tenant
        // recovery/exfiltration backdoor or an existence oracle.
        var replayOther = await Client(host, GameAdminA).GetAsync("/api/v1/admin/rooms/tenant-b/some-room/replay");
        Assert.Equal(HttpStatusCode.Forbidden, replayOther.StatusCode);

        // tenant-a's caller attaches the live SSE observe stream for a tenant-b room: same 403.
        var observeStreamOther = await Client(host, GameAdminA).GetAsync("/api/v1/admin/rooms/tenant-b/some-room/observe");
        Assert.Equal(HttpStatusCode.Forbidden, observeStreamOther.StatusCode);

        _output.WriteLine("no cross-tenant enumeration: list filtered, get/terminate/observe/replay/observe-stream of another tenant => 403 (not 404)");
    }

    [Fact]
    public async Task RoleEscalation_LesserRoleCannotPerformHigherOp_NorActForAnotherTenant()
    {
        using var host = new WebApplicationFactory<Program>();
        var audit = host.Services.GetRequiredService<IAuditLog>();

        // A read-only operator cannot mutate (create a game) — least privilege.
        var operatorMutate = await Client(host, OperatorA).PostAsJsonAsync("/api/v1/games",
            new { tenantId = "tenant-a", gameId = "esc1", name = "x", description = "", protocolVersion = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, operatorMutate.StatusCode);

        // A game-admin cannot perform a platform-level op (create a tenant).
        var gameAdminPlatform = await Client(host, GameAdminA).PostAsJsonAsync("/api/v1/tenants",
            new { tenantId = "esc-tenant", displayName = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, gameAdminPlatform.StatusCode);

        // A game-admin scoped to tenant-b cannot act for tenant-a (create a game owned by tenant-a).
        var crossTenantMutate = await Client(host, GameAdminB).PostAsJsonAsync("/api/v1/games",
            new { tenantId = "tenant-a", gameId = "esc2", name = "x", description = "", protocolVersion = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, crossTenantMutate.StatusCode);

        // Every one of these escalation attempts is recorded as a denial (audit completeness on denials).
        Assert.Contains(audit.Read(), r => r.Action == "create-game" && r.Outcome == "denied");
        Assert.Contains(audit.Read(), r => r.Action == "create-tenant" && r.Outcome == "denied");

        _output.WriteLine("role escalation denied: operator!=mutate, game-admin!=platform, cross-tenant!=allowed; all audited");
    }

    [Fact]
    public async Task RoomTerminateGoesThroughAuthoritativePath_ObservableAudited_NoOrphanState()
    {
        using var host = new WebApplicationFactory<Program>();
        var server = host.Services.GetRequiredService<RealtimeServer>();
        var audit = host.Services.GetRequiredService<IAuditLog>();

        // Seed a LIVE room in the real host's RealtimeServer by driving a join in-process. The real host
        // reaps a room when its last subscriber disconnects, so we keep the connection OPEN (the loop parks
        // awaiting more input) for the duration of the test instead of completing the client.
        const string tenant = "tenant-a", game = "demo", room = "arena", player = "p1";
        var transport = new InMemoryBidirectionalTransport(new ConnectionId("c-term"));
        transport.ClientSend(Envelope(MessageType.ClientHello, new ClientHello(player), tenant, game, null, player));
        transport.ClientSend(Envelope(MessageType.ClientJoinRoom, new ClientJoinRoom(new RoomId(room)), tenant, game, room, player));
        var claims = new JoinTokenClaims(tenant, game, room, player, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1));
        var loop = server.HandleConnectionAsync(transport, claims);

        var key = new RoomKey(new TenantId(tenant), new RoomId(room));
        // The join is processed synchronously up to the first await (the next ReceiveAsync), so the room is
        // live by the time the queued frames are drained. Wait briefly for it to register.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!server.ActiveRooms.Contains(key) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Contains(key, server.ActiveRooms);

        // 403: a read-only operator cannot terminate (it is a mutation).
        var operatorTerminate = await Client(host, OperatorA).PostAsync($"/api/v1/admin/rooms/{tenant}/{room}/terminate", content: null);
        Assert.Equal(HttpStatusCode.Forbidden, operatorTerminate.StatusCode);
        Assert.Contains(key, server.ActiveRooms); // denial did not touch the room

        // 200: a game-admin of tenant-a terminates through the authoritative pathway.
        var terminate = await Client(host, GameAdminA).PostAsync($"/api/v1/admin/rooms/{tenant}/{room}/terminate", content: null);
        Assert.Equal(HttpStatusCode.OK, terminate.StatusCode);

        // No orphan state: the room is gone from the live set and can no longer be observed.
        Assert.DoesNotContain(key, server.ActiveRooms);
        Assert.False(server.TryObserveRoom(key, out _));

        // Observable + audited.
        Assert.Contains(audit.Read(), r => r.Action == "terminate-room" && r.Outcome == "allowed" && r.Target == $"{tenant}/{room}");

        // Terminating an already-gone room => 404 (idempotent at the HTTP layer).
        var again = await Client(host, GameAdminA).PostAsync($"/api/v1/admin/rooms/{tenant}/{room}/terminate", content: null);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);

        // End the parked connection loop cleanly.
        transport.CompleteClient();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));

        _output.WriteLine("room terminated through the authoritative path: gone from ActiveRooms, unobservable, audited");
    }

    private static MessageEnvelope Envelope(MessageType type, IMessagePayload payload, string tenant, string game, string? room, string player) =>
        new()
        {
            TenantId = new TenantId(tenant),
            GameId = new GameId(game),
            RoomId = room is null ? null : new RoomId(room),
            SessionId = null,
            PlayerId = new PlayerId(player),
            ProtocolVersion = ProtocolVersions.Current,
            MessageType = type,
            Sequence = 0,
            TraceId = $"{type}-{player}",
            Payload = payload,
        };
}
