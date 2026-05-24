using System.Text.Json.Serialization;
using GameServer.ControlPlane;
using GameServer.Host;
using GameServer.Identity;
using GameServer.Observability;
using GameServer.Persistence;
using GameServer.Protocol;
using GameServer.Protocol.Realtime;
using GameServer.Replication;
using GameServer.Routing;
using GameServer.Simulation;
using GameServer.Tenancy;
using GameServer.Transport;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5000");

// Render enums (e.g. ReplicationPolicy modes) as readable strings in the HTTP API.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// ---- Realtime kernel (reused as-is) -----------------------------------------
builder.Services.AddSingleton<IMessageCodec, JsonMessageCodec>(); // dev/debug JSON codec only
builder.Services.AddSingleton<RealtimeProtobufCodec>();           // canonical binary codec
builder.Services.AddSingleton<RealtimeEnvelopeMapper>();
// Aggregating telemetry: the hot path only accumulates counters; TelemetryFlushService
// reports rolling rates periodically (no per-message I/O).
builder.Services.AddSingleton<AggregatingTelemetrySink>();
builder.Services.AddSingleton<ITelemetrySink>(sp => sp.GetRequiredService<AggregatingTelemetrySink>());
builder.Services.AddSingleton<ITenantResolver>(_ => new InMemoryTenantResolver(new[]
{
    new TenantContext(new TenantId("tenant-a"), "Tenant A"),
    new TenantContext(new TenantId("tenant-b"), "Tenant B"),
}));
builder.Services.AddSingleton<ISnapshotStore<RoomKey, RoomSnapshot>>(_ => new InMemorySnapshotStore<RoomKey, RoomSnapshot>());
builder.Services.AddSingleton<IEventLog<RoomKey, RoomEvent>>(_ => new InMemoryEventLog<RoomKey, RoomEvent>());
builder.Services.AddSingleton<ISessionRouter>(_ => new InMemorySessionRouter(
    (roomId, gameId) => new GameRoom(roomId, GameFor(gameId), new LogicalSimulationClock(), new SeededRandomSource())));
// Per-game replication policy comes from the control-plane catalog (registered below).
builder.Services.AddSingleton<RealtimeServer>(sp => new RealtimeServer(
    sp.GetRequiredService<ITenantResolver>(),
    sp.GetRequiredService<ISessionRouter>(),
    sp.GetRequiredService<ISnapshotStore<RoomKey, RoomSnapshot>>(),
    sp.GetRequiredService<IEventLog<RoomKey, RoomEvent>>(),
    sp.GetRequiredService<ITelemetrySink>(),
    gameId => sp.GetRequiredService<InMemoryGameCatalog>().GetPolicy(gameId.Value)));
builder.Services.AddHostedService<RoomTickService>();
builder.Services.AddHostedService<TelemetryFlushService>();

// ---- Identity (edge auth) ---------------------------------------------------
// Signed, short-lived, scoped join tokens. The control plane issues them; the
// realtime edge verifies them statelessly. The secret MUST be overridden in
// production via configuration (Auth:JoinTokenSecret) — the fallback is dev-only.
var joinTokenSecret = builder.Configuration["Auth:JoinTokenSecret"]
    ?? "dev-only-insecure-join-token-secret-change-me";
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<Hs256JoinTokenCodec>(sp => new Hs256JoinTokenCodec(
    joinTokenSecret, sp.GetRequiredService<IClock>(), TimeSpan.FromMinutes(2)));
builder.Services.AddSingleton<IJoinTokenIssuer>(sp => sp.GetRequiredService<Hs256JoinTokenCodec>());
builder.Services.AddSingleton<IJoinTokenVerifier>(sp => sp.GetRequiredService<Hs256JoinTokenCodec>());

// ---- Control plane (HTTP) ---------------------------------------------------
// Use the seeding constructor explicitly: DI would otherwise pick the greediest
// constructor and resolve IEnumerable<GameDetail> to an empty set.
builder.Services.AddSingleton<InMemoryGameCatalog>(_ => new InMemoryGameCatalog());
builder.Services.AddSingleton<InMemoryRoomRegistry>();
builder.Services.AddSingleton<JoinTokenService>();
builder.Services.AddSingleton<InMemorySessionRegistry>();

// Control-plane caller auth: API keys presented as `Authorization: Bearer <key>`.
// Dev keys only — production configures real keys or swaps in an IdP-backed
// IControlPlaneAuthenticator without touching the endpoints.
builder.Services.AddSingleton<IControlPlaneAuthenticator>(new ApiKeyControlPlaneAuthenticator(
    new Dictionary<string, CallerPrincipal>
    {
        ["dev-tenant-a-key"] = new("dev-operator-a", "tenant-a", new HashSet<string>()),
        ["dev-tenant-b-key"] = new("dev-operator-b", "tenant-b", new HashSet<string>()),
        ["dev-admin-key"] = new("dev-admin", "tenant-a", new HashSet<string> { CallerPrincipal.PlatformAdminRole }),
    }));
builder.Services.AddSingleton<IAuditLog, InMemoryAuditLog>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets();

// ============================================================================
// Control-plane HTTP API. JSON DTOs; session lifecycle only; no gameplay logic.
// ============================================================================
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/ready", () => Results.Ok(new { status = "ready" }));
app.MapGet("/version", () => Results.Ok(new
{
    protocolVersion = ProtocolVersions.Current,
    realtimeProtocol = "gameserver.realtime.v1",
}));

// Every /api/v1 endpoint requires an authenticated caller. The group filter
// authenticates once (401 on failure); tenant-scoped operations then authorize the
// caller's tenant (403 on mismatch) and audit the outcome.
var api = app.MapGroup("/api/v1");
api.AddEndpointFilter(async (ctx, next) =>
{
    var authenticator = ctx.HttpContext.RequestServices.GetRequiredService<IControlPlaneAuthenticator>();
    if (!authenticator.TryAuthenticate(ctx.HttpContext.Request.Headers.Authorization.ToString(), out var caller))
    {
        return Results.Json(
            new ApiError("Unauthorized", "Missing or invalid API credentials."),
            statusCode: StatusCodes.Status401Unauthorized);
    }

    ctx.HttpContext.Items["caller"] = caller;
    return await next(ctx);
});

// Game catalog is platform-global: any authenticated caller may read it.
api.MapGet("/games", (InMemoryGameCatalog catalog) => Results.Ok(catalog.List()));

api.MapGet("/games/{gameId}", (string gameId, InMemoryGameCatalog catalog) =>
    catalog.TryGet(gameId, out var game)
        ? Results.Ok(game)
        : Results.NotFound(new ApiError("GameNotFound", $"Game '{gameId}' was not found.")));

api.MapPost("/rooms", (HttpContext ctx, CreateRoomRequest request, InMemoryGameCatalog catalog, InMemoryRoomRegistry rooms, IAuditLog audit, IClock clock) =>
{
    if (Forbid(ctx, request.TenantId, "create-room", $"{request.TenantId}/{request.GameId}", audit, clock) is { } denied)
    {
        return denied;
    }

    if (!catalog.TryGet(request.GameId, out _))
    {
        return Results.NotFound(new ApiError("GameNotFound", $"Game '{request.GameId}' was not found."));
    }

    var room = rooms.Create(request);
    Audit(ctx, "create-room", $"{room.TenantId}/{room.RoomId}", audit, clock);
    return Results.Created($"/api/v1/rooms/{room.RoomId}", room);
});

api.MapGet("/rooms/{roomId}", (HttpContext ctx, string roomId, InMemoryRoomRegistry rooms, IAuditLog audit, IClock clock) =>
{
    if (!rooms.TryGet(roomId, out var room))
    {
        return Results.NotFound(new ApiError("RoomNotFound", $"Room '{roomId}' was not found."));
    }

    // A room is tenant data: a caller may not read another tenant's room.
    return Forbid(ctx, room.TenantId, "read-room", $"{room.TenantId}/{room.RoomId}", audit, clock) ?? Results.Ok(room);
});

api.MapPost("/rooms/{roomId}/join-token",
    (HttpContext ctx, string roomId, CreateJoinTokenRequest request, InMemoryRoomRegistry rooms, JoinTokenService tokens, IAuditLog audit, IClock clock) =>
{
    if (!rooms.TryGet(roomId, out var room))
    {
        return Results.NotFound(new ApiError("RoomNotFound", $"Room '{roomId}' was not found."));
    }

    var target = $"{room.TenantId}/{room.RoomId}/{request.PlayerId}";
    if (Forbid(ctx, room.TenantId, "mint-join-token", target, audit, clock) is { } denied)
    {
        return denied;
    }

    var token = tokens.Issue(room.TenantId, room.GameId, room.RoomId, request.PlayerId);
    Audit(ctx, "mint-join-token", target, audit, clock);
    return Results.Created($"/realtime/v1/connect?joinToken={token.Token}", token);
});

api.MapPost("/sessions", (HttpContext ctx, CreateSessionRequest request, InMemorySessionRegistry sessions, IAuditLog audit, IClock clock) =>
{
    if (Forbid(ctx, request.TenantId, "create-session", $"{request.TenantId}/{request.PlayerId}", audit, clock) is { } denied)
    {
        return denied;
    }

    var session = sessions.Create(request);
    Audit(ctx, "create-session", $"{request.TenantId}/{session.SessionId}", audit, clock);
    return Results.Created($"/api/v1/sessions/{session.SessionId}", session);
});

api.MapDelete("/sessions/{sessionId}", (HttpContext ctx, string sessionId, InMemorySessionRegistry sessions, IAuditLog audit, IClock clock) =>
{
    if (!sessions.TryGet(sessionId, out var session))
    {
        return Results.NotFound(new ApiError("SessionNotFound", $"Session '{sessionId}' was not found."));
    }

    if (Forbid(ctx, session.TenantId, "delete-session", $"{session.TenantId}/{sessionId}", audit, clock) is { } denied)
    {
        return denied;
    }

    sessions.Remove(sessionId);
    Audit(ctx, "delete-session", $"{session.TenantId}/{sessionId}", audit, clock);
    return Results.NoContent();
});

// ============================================================================
// Realtime gameplay transport: binary protobuf only. Requires a join token.
// ============================================================================
app.Map("/realtime/v1/connect", branch => branch.Run(async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new ApiError("MalformedFrame", "A WebSocket upgrade is required."));
        return;
    }

    var joinToken = context.Request.Query["joinToken"].ToString();
    var verification = context.RequestServices.GetRequiredService<IJoinTokenVerifier>().Verify(joinToken);
    if (!verification.IsOk)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new ApiError("Unauthorized", "Missing or invalid joinToken."));
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var transport = new ProtobufRealtimeTransport(
        new AspNetRealtimeChannel(socket),
        context.RequestServices.GetRequiredService<RealtimeProtobufCodec>(),
        context.RequestServices.GetRequiredService<RealtimeEnvelopeMapper>(),
        new ConnectionId(Guid.NewGuid().ToString("n")));

    // The verified claims are authoritative for this connection's identity.
    await context.RequestServices.GetRequiredService<RealtimeServer>()
        .HandleConnectionAsync(transport, verification.Claims!, context.RequestAborted);
}));

// Dev/debug only: a JSON realtime codec for browser exploration. NOT the contract.
if (app.Environment.IsDevelopment())
{
    app.Map("/ws", branch => branch.Run(async context =>
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var transport = new WebSocketChannelTransport(
            new AspNetWebSocketChannel(socket),
            context.RequestServices.GetRequiredService<IMessageCodec>(),
            new ConnectionId(Guid.NewGuid().ToString("n")));

        // Dev-only: this debug endpoint TRUSTS the identity supplied on the query string
        // instead of verifying a signed join token. It exists solely for in-browser
        // exploration under Development; the production realtime path
        // (/realtime/v1/connect) always verifies a token.
        var devPrincipal = new JoinTokenClaims(
            context.Request.Query["tenant"].ToString(),
            context.Request.Query["game"].ToString(),
            context.Request.Query["room"].ToString(),
            context.Request.Query["player"].ToString(),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.MaxValue);

        await context.RequestServices.GetRequiredService<RealtimeServer>()
            .HandleConnectionAsync(transport, devPrincipal, context.RequestAborted);
    }));
}

app.Run();

// ---- Control-plane authorization + audit helpers ----------------------------
// The group filter has already authenticated the caller into HttpContext.Items.

// Returns a 403 result (and audits the denial) when the caller may not act for the
// given tenant; returns null when the caller is authorized.
static IResult? Forbid(HttpContext ctx, string tenantId, string action, string target, IAuditLog audit, IClock clock)
{
    var caller = (CallerPrincipal)ctx.Items["caller"]!;
    if (caller.CanActFor(tenantId))
    {
        return null;
    }

    audit.Record(new AuditRecord(caller.CallerId, action, target, clock.UtcNow, "denied"));
    return Results.Json(
        new ApiError("Forbidden", $"Caller is not authorized for tenant '{tenantId}'."),
        statusCode: StatusCodes.Status403Forbidden);
}

// Records a successful, authorized mutation.
static void Audit(HttpContext ctx, string action, string target, IAuditLog audit, IClock clock)
{
    var caller = (CallerPrincipal)ctx.Items["caller"]!;
    audit.Record(new AuditRecord(caller.CallerId, action, target, clock.UtcNow, "allowed"));
}

// Selects the game implementation for a room by its game id. (Step 3 will source this
// from the control-plane catalog; for now it mirrors the catalog's seeded games.)
static IGameSimulation GameFor(GameId gameId) => gameId.Value switch
{
    "grid-walk" => new GridWalkGame(),
    _ => new MoveRightGame(),
};
