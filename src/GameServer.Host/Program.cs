using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameServer.Cluster.Redis;
using GameServer.ControlPlane;
using GameServer.Host;
using GameServer.Identity;
using GameServer.Matchmaking;
using GameServer.Observability;
using GameServer.Persistence;
using GameServer.Persistence.Postgres;
using GameServer.Protocol;
using GameServer.Protocol.Realtime;
using GameServer.Replication;
using GameServer.Routing;
using GameServer.Simulation;
using GameServer.Tenancy;
using GameServer.Transport;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
// Bind address: honor the standard ASP.NET Core env vars when present (e.g. in-container the
// runtime image sets ASPNETCORE_HTTP_PORTS=8080 so Kestrel binds 0.0.0.0:8080). Calling
// UseUrls unconditionally would HARD-OVERRIDE those env vars and pin the host to localhost:5000,
// which cannot be reached from outside a container. So we only fall back to the local-dev default
// when neither ASPNETCORE_URLS nor ASPNETCORE_HTTP_PORTS is configured.
if (string.IsNullOrEmpty(builder.Configuration["ASPNETCORE_URLS"])
    && string.IsNullOrEmpty(builder.Configuration["urls"])
    && string.IsNullOrEmpty(builder.Configuration["ASPNETCORE_HTTP_PORTS"])
    && string.IsNullOrEmpty(builder.Configuration["http_ports"]))
{
    builder.WebHost.UseUrls("http://localhost:5000");
}

// ---- Cluster identity (used by the realtime kernel and the clustering services below) -------
// NodeId is this node's externally-reachable base address, so an affinity redirect can carry it
// directly. The fleet is the set of nodes placement may assign rooms to (defaults to just this node).
var localNode = new NodeId(builder.Configuration["Cluster:NodeId"] ?? "http://localhost:5000");
var fleetNodes = builder.Configuration.GetSection("Cluster:Nodes").Get<string[]>() is { Length: > 0 } configuredNodes
    ? configuredNodes.Select(n => new NodeId(n)).ToArray()
    : new[] { localNode };
var maxRoomsPerNode = builder.Configuration.GetValue("Cluster:MaxRoomsPerNode", 10_000);
// Reserved headroom: placement fills a node only up to (MaxRoomsPerNode - ReservedHeadroom), keeping
// spare slots free to absorb rooms shed from a draining peer and to ride out a burst without driving a
// node to its absolute ceiling. 0 = fill to the ceiling (the prior behavior).
var reservedHeadroom = builder.Configuration.GetValue("Cluster:ReservedHeadroom", 0);

// Do not advertise the server implementation. The `Server: Kestrel` response banner is free
// reconnaissance for an attacker (it names the stack and, across versions, narrows known-CVE
// fingerprinting) and buys a legitimate client nothing. Suppressing it is defense-in-depth, not a
// substitute for patching. Pinned by ServerHeaderSuppressedScenario.
builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false);

// Render enums (e.g. ReplicationPolicy modes) as readable strings in the HTTP API.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Bound graceful shutdown. The realtime connection loops are linked to ApplicationStopping
// (see the realtime endpoints below), so they end promptly on Ctrl+C; this cap guarantees
// that even a wedged transport or background worker cannot hold the process open beyond the
// budget — the symptom of an unkillable server is a lifecycle defect, not an OS quirk.
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));

// ---- Realtime kernel (reused as-is) -----------------------------------------
builder.Services.AddSingleton<IMessageCodec, JsonMessageCodec>(); // dev/debug JSON codec only
builder.Services.AddSingleton<RealtimeProtobufCodec>();           // canonical binary codec
builder.Services.AddSingleton<RealtimeEnvelopeMapper>();
// Aggregating telemetry: the hot path only accumulates counters; TelemetryFlushService
// reports rolling rates periodically (no per-message I/O).
builder.Services.AddSingleton<AggregatingTelemetrySink>();
builder.Services.AddSingleton<ITelemetrySink>(sp => sp.GetRequiredService<AggregatingTelemetrySink>());
// Tenants are RESOLVED from the control-plane registry, not hardcoded: provisioning a tenant is a
// runtime API call (POST /api/v1/tenants), so adding one is no longer a code change. The same registry
// instance the CRUD endpoints mutate is the one the realtime edge resolves through (it implements
// ITenantResolver), so a newly-provisioned tenant is immediately resolvable on the hot path with no
// redeploy. The default in-memory registry is seeded for dev/single-node; it stays the hot-path read
// (an in-memory lookup, never a DB round-trip per command). When Persistence:Backend=Postgres, the seed
// is the configured per-tenant connection map (the tenants that actually have a database).
var seededTenants = builder.Configuration.GetSection("ControlPlane:Tenants").Get<List<TenantSeedEntry>>();
IEnumerable<TenantRecordDto> tenantSeed = seededTenants is { Count: > 0 }
    ? seededTenants.Select(t => new TenantRecordDto(
        t.TenantId ?? throw new InvalidOperationException("A ControlPlane:Tenants entry is missing TenantId."),
        t.DisplayName ?? t.TenantId!))
    : new[] { new TenantRecordDto("tenant-a", "Tenant A"), new TenantRecordDto("tenant-b", "Tenant B") };
builder.Services.AddSingleton<InMemoryTenantRegistry>(_ => new InMemoryTenantRegistry(tenantSeed));
builder.Services.AddSingleton<ITenantRegistry>(sp => sp.GetRequiredService<InMemoryTenantRegistry>());
builder.Services.AddSingleton<ITenantResolver>(sp => sp.GetRequiredService<InMemoryTenantRegistry>());
// Durable persistence backend is config-selected, exactly like the cluster directory: in-memory for
// dev / a single throwaway node (state dies with the process), Postgres for a durable, multi-tenant
// deployment — both behind the SAME rank-0 ISnapshotStore / IEventLog ports, so the recovery path is
// unchanged. Postgres is DATABASE-PER-TENANT: a room's key projects to (tenant, room) and the tenant
// part selects that tenant's own database via the connection resolver, so a write can never land in a
// shared table another tenant could read.
if (string.Equals(builder.Configuration["Persistence:Backend"], "Postgres", StringComparison.OrdinalIgnoreCase))
{
    // Per-tenant connection map (Persistence:Postgres:Tenants:<tenantId> = connection string). An
    // unmapped tenant FAILS rather than falling back to a shared database (see the resolver).
    var tenantConnections = builder.Configuration.GetSection("Persistence:Postgres:Tenants")
        .GetChildren()
        .ToDictionary(c => c.Key, c => c.Value
            ?? throw new InvalidOperationException($"Persistence:Postgres:Tenants:{c.Key} has no connection string."));
    if (tenantConnections.Count == 0)
    {
        throw new InvalidOperationException(
            "Persistence:Backend=Postgres requires at least one per-tenant connection under " +
            "Persistence:Postgres:Tenants. Refusing to start a durable host with no tenant database mapping.");
    }

    builder.Services.AddSingleton<ITenantConnectionResolver>(new DictionaryTenantConnectionResolver(tenantConnections));
    builder.Services.AddSingleton<ITenantDbContextFactory>(sp =>
        new TenantDbContextFactory(sp.GetRequiredService<ITenantConnectionResolver>()));

    // Project the rank-2 RoomKey into the adapter's tenant-scoped DurableRoomKey at the composition
    // root (the only place that knows both types). This keeps the Postgres adapter free of RoomKey.
    static DurableRoomKey KeyOf(RoomKey key) => new(key.TenantId.Value, key.RoomId.Value);

    builder.Services.AddSingleton<ISnapshotStore<RoomKey, RoomSnapshot>>(sp =>
        new PostgresSnapshotStore<RoomKey>(sp.GetRequiredService<ITenantDbContextFactory>(), KeyOf));
    builder.Services.AddSingleton<IEventLog<RoomKey, RoomEvent>>(sp =>
        new PostgresEventLog<RoomKey>(sp.GetRequiredService<ITenantDbContextFactory>(), KeyOf));

    // Bring each mapped tenant's schema up to date on startup (idempotent), so the node is ready to
    // persist immediately. Migration is applied once per tenant database.
    var contexts = new TenantDbContextFactory(new DictionaryTenantConnectionResolver(tenantConnections));
    foreach (var tenantId in tenantConnections.Keys)
    {
        contexts.Migrate(tenantId);
    }
}
else
{
    builder.Services.AddSingleton<ISnapshotStore<RoomKey, RoomSnapshot>>(_ => new InMemorySnapshotStore<RoomKey, RoomSnapshot>());
    // The tick selector lets the log compact events folded into a snapshot (see retention below).
    builder.Services.AddSingleton<IEventLog<RoomKey, RoomEvent>>(_ => new InMemoryEventLog<RoomKey, RoomEvent>(e => e.Tick));
}
builder.Services.AddSingleton<ISessionRouter>(_ => new InMemorySessionRouter(
    (roomId, gameId) => new GameRoom(roomId, GameFor(gameId), new LogicalSimulationClock(), new SeededRandomSource())));
// Admission ceilings are enforced at the edge so a burst sheds cleanly instead of driving the
// process over capacity. Configurable (Realtime:*) with finite defaults sized for this node;
// never unbounded in a deployed host.
var admissionPolicy = new AdmissionPolicy(
    MaxConnections: builder.Configuration.GetValue("Realtime:MaxConnections", 100_000),
    MaxConnectionsPerTenant: builder.Configuration.GetValue("Realtime:MaxConnectionsPerTenant", 25_000),
    MaxRooms: builder.Configuration.GetValue("Realtime:MaxRooms", 50_000),
    MaxRoomsPerTenant: builder.Configuration.GetValue("Realtime:MaxRoomsPerTenant", 10_000));
// ---- Graceful degradation ladder (Wave 6) -----------------------------------
// The authoritative cadence is needed up front: it is BOTH the tick driver's interval AND the
// degradation controller's per-tick budget (a cycle longer than the interval is a missed tick). Read it
// here so the controller, the RealtimeServer edge, and the tick driver all agree on one budget.
var tickHz = builder.Configuration.GetValue("Realtime:TickHz", RoomTickService.DefaultTickHz);
var tickBudgetMs = RoomTickService.IntervalForHz(tickHz).TotalMilliseconds;
// The ladder turns the tick driver's observed health (missed_ticks / tick p95) into an explicit, ordered
// decision (reduce spectator snapshots → shed telemetry → reject new rooms → reject new connections), with
// immediate escalation and one-step hysteresis on recovery so it never flaps. Registered as a REQUIRED
// singleton (not a dormant null-guarded seam): the tick driver feeds it and the realtime edge consults it
// on the hot path — both resolve it from DI, so it is always non-null in the deployed host.
builder.Services.AddSingleton<IDegradationController>(_ => new LadderDegradationController(tickBudgetMs));

// Per-game replication policy comes from the control-plane catalog (registered below).
builder.Services.AddSingleton<RealtimeServer>(sp => new RealtimeServer(
    sp.GetRequiredService<ITenantResolver>(),
    sp.GetRequiredService<ISessionRouter>(),
    sp.GetRequiredService<ISnapshotStore<RoomKey, RoomSnapshot>>(),
    sp.GetRequiredService<IEventLog<RoomKey, RoomEvent>>(),
    sp.GetRequiredService<ITelemetrySink>(),
    gameId => sp.GetRequiredService<IGameRegistry>().GetPolicy(gameId.Value),
    // Reap empty rooms so memory tracks live rooms, not every room ever joined.
    RoomLifecycle.Reap,
    admission: admissionPolicy,
    // Keep a trailing window of room events; older events are covered by the saved
    // snapshot and compacted away, so the event log cannot grow without bound.
    eventLogRetentionTicks: 256,
    // Release this node's room ownership claim as a room is reaped, so directory ownership tracks
    // live rooms (otherwise OwnedCount only grows and placement eventually wedges).
    roomDirectory: sp.GetRequiredService<IRoomDirectory>(),
    localNode: localNode,
    // Per-tenant fairness + attribution + a kernel-level command-size bound, enforced ON the hot
    // path (see RealtimeServer.HandleCommandAsync / HandleConnectionAsync) — not dormant seams.
    rateLimiter: sp.GetRequiredService<ITenantRateLimiter>(),
    tenantMetrics: sp.GetRequiredService<ITenantMetricsSink>(),
    // Defense-in-depth behind the transport frame cap (Realtime:MaxFrameBytes): a decoded command
    // larger than this is shed with a typed error and the connection survives. Defaults to the frame
    // cap so the two bounds agree; a single value keeps the story simple.
    maxCommandBytes: builder.Configuration.GetValue("Realtime:MaxCommandBytes", AspNetRealtimeChannel.DefaultMaxMessageBytes),
    // Graceful-degradation ladder, consulted ON the hot path (HandleConnectionAsync / HandleJoinAsync /
    // TickRoom): refuse new connections/rooms and shed optional telemetry/spectator pushes under sustained
    // tick overload, never the authoritative simulation. Required here; the tick driver feeds it.
    degradation: sp.GetRequiredService<IDegradationController>()));
// Rooms are independent state owners, so tick them concurrently across all cores; one
// slow room must not block the rest (head-of-line blocking).
builder.Services.AddSingleton<IRoomTickScheduler>(_ => ParallelRoomTickScheduler.ForProcessorCount());
// Bounded per-room tick-cost telemetry so ops can find the hot room (the global sink can't).
builder.Services.AddSingleton<RoomScopedMetrics>(_ => new RoomScopedMetrics(maxRooms: 1024));
// Bounded per-tenant inbound attribution so ops can NAME the noisy tenant during an incident (the
// global sink folds tags away). Fed from the realtime edge (see RealtimeServer); exposed both as the
// concrete type (admin reads) and the rank-0 ITenantMetricsSink port (the edge writes through it).
builder.Services.AddSingleton<TenantScopedMetrics>(_ => new TenantScopedMetrics(maxTenants: 4096));
builder.Services.AddSingleton<ITenantMetricsSink>(sp => sp.GetRequiredService<TenantScopedMetrics>());
// Per-tenant ingress rate limiting — the core noisy-neighbor control on the realtime command path.
// Each tenant gets its own token bucket (independent buckets), so one tenant's flood is throttled to
// its fair share and shed cleanly without touching another tenant's allowance. Sized from config;
// reads time through the monotonic-clock seam so refill is testable.
builder.Services.AddSingleton<IMonotonicClock, SystemMonotonicClock>();
var tenantPermitsPerSecond = builder.Configuration.GetValue("Realtime:TenantCommandsPerSecond", 2_000);
var tenantBurst = builder.Configuration.GetValue("Realtime:TenantCommandBurst", 4_000);
builder.Services.AddSingleton<ITenantRateLimiter>(sp => new TokenBucketTenantRateLimiter(
    tenantPermitsPerSecond, tenantBurst, sp.GetRequiredService<IMonotonicClock>()));
// Long-running loops run as supervised workers, not bare hosted services: the supervisor
// restarts a faulting worker (counted as worker_restart_count) instead of letting an
// unhandled fault stop the whole host, and drains them within a bounded budget on shutdown.
// Authoritative cadence is configurable (Realtime:TickHz, default 10 Hz) so a scenario can
// drive 30 Hz without recompiling; the simulation kernel stays wall-clock-free — only this
// host-side driver knows the rate. The driver also closes the degradation observe→act loop:
// it feeds each cycle's health to the ladder it shares with the edge (tickHz read above).
builder.Services.AddSingleton<RoomTickService>(sp => new RoomTickService(
    sp.GetRequiredService<RealtimeServer>(),
    sp.GetRequiredService<IRoomTickScheduler>(),
    sp.GetRequiredService<ITelemetrySink>(),
    sp.GetRequiredService<RoomScopedMetrics>(),
    sp.GetRequiredService<ILogger<RoomTickService>>(),
    sp.GetRequiredService<IDegradationController>(),
    tickHz));
builder.Services.AddSingleton<ISupervisedWorker>(sp => sp.GetRequiredService<RoomTickService>());
builder.Services.AddSingleton<TelemetryFlushService>();
builder.Services.AddSingleton<ISupervisedWorker>(sp => sp.GetRequiredService<TelemetryFlushService>());
builder.Services.AddHostedService<SupervisorHostedService>();

// ---- Identity (edge auth) ---------------------------------------------------
// Signed, short-lived, scoped join tokens. The control plane issues them; the
// realtime edge verifies them statelessly. The secret signs every join token, so a
// known/weak secret means an attacker can forge tokens for any tenant/room/player.
// In Development a fixed dev secret keeps the local workflow frictionless; in any other
// environment the host FAILS TO START unless a strong, non-default secret is configured
// (Auth:JoinTokenSecret) — failing closed beats silently signing with a forgeable key.
const string DevJoinTokenSecret = "dev-only-insecure-join-token-secret-change-me";
string joinTokenSecret;
if (builder.Environment.IsDevelopment())
{
    joinTokenSecret = builder.Configuration["Auth:JoinTokenSecret"] ?? DevJoinTokenSecret;
}
else
{
    joinTokenSecret = builder.Configuration["Auth:JoinTokenSecret"]
        ?? throw new InvalidOperationException(
            "Auth:JoinTokenSecret is not configured. The realtime edge cannot verify join tokens " +
            "without a signing secret; refusing to start outside Development.");
    if (joinTokenSecret == DevJoinTokenSecret || joinTokenSecret.Length < 32)
    {
        throw new InvalidOperationException(
            "Auth:JoinTokenSecret must be a strong, non-default secret (at least 32 characters) " +
            "outside Development. A known or short secret allows join-token forgery.");
    }
}
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<Hs256JoinTokenCodec>(sp => new Hs256JoinTokenCodec(
    joinTokenSecret, sp.GetRequiredService<IClock>(), TimeSpan.FromMinutes(2)));
builder.Services.AddSingleton<IJoinTokenIssuer>(sp => sp.GetRequiredService<Hs256JoinTokenCodec>());
builder.Services.AddSingleton<IJoinTokenVerifier>(sp => sp.GetRequiredService<Hs256JoinTokenCodec>());

// ---- Control plane (HTTP) ---------------------------------------------------
// The game catalog is the control-plane game REGISTRY: games and game versions are registered at runtime
// via the CRUD endpoints, and the realtime edge reads the same instance for per-game replication policy
// (so a game added via the API is usable by new rooms with no code change). The in-memory registry is the
// default (seeded for dev). When Persistence:Backend=Postgres, the durable registry persists each game in
// its OWNING tenant's database (the Wave-3 games/game_versions tables) and serves hot-path reads from a
// warm cache loaded once at startup — no DB call per command. Both behind the same IGameRegistry port.
// Use the seeding constructor explicitly: DI would otherwise pick the greediest constructor.
builder.Services.AddSingleton<InMemoryGameCatalog>(_ => new InMemoryGameCatalog());
if (string.Equals(builder.Configuration["Persistence:Backend"], "Postgres", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IGameRegistry>(sp =>
    {
        var factory = sp.GetRequiredService<ITenantDbContextFactory>();
        var registryTenants = sp.GetRequiredService<InMemoryTenantRegistry>().List().Select(t => t.TenantId).ToArray();
        // Replication policy is platform config (not persisted): mirror the in-memory catalog's defaults so
        // a durable game keeps the same hot-path behavior its id implies.
        var seedCatalog = sp.GetRequiredService<InMemoryGameCatalog>();
        ReplicationPolicy? PolicyFor(string gameId) => seedCatalog.TryGet(gameId, out var g) ? g.Replication : null;
        var seeds = PostgresGameRegistry.Load(factory, registryTenants, PolicyFor);
        return new PostgresGameRegistry(factory, seeds);
    });
}
else
{
    builder.Services.AddSingleton<IGameRegistry>(sp => sp.GetRequiredService<InMemoryGameCatalog>());
}
builder.Services.AddSingleton<InMemoryRoomRegistry>();
builder.Services.AddSingleton<JoinTokenService>();
builder.Services.AddSingleton<InMemorySessionRegistry>();

// Control-plane caller auth: API keys presented as `Authorization: Bearer <key>`.
// In Development a fixed set of dev keys is seeded for local exploration. Outside
// Development the dev keys are NEVER present; keys come from configuration
// (ControlPlane:ApiKeys) and the host fails closed if none are configured — an
// unauthenticated control plane is worse than one that will not start. Swapping in an
// IdP-backed IControlPlaneAuthenticator remains a one-component change behind the seam.
Dictionary<string, CallerPrincipal> controlPlaneApiKeys;
if (builder.Environment.IsDevelopment())
{
    controlPlaneApiKeys = new Dictionary<string, CallerPrincipal>
    {
        // No-role caller: authenticates but holds no admin capability (cannot read admin views, cannot
        // mutate). Kept for the existing tenant-scoped session/room/matchmaking endpoints.
        ["dev-tenant-a-key"] = new("dev-operator-a", "tenant-a", new HashSet<string>()),
        ["dev-tenant-b-key"] = new("dev-operator-b", "tenant-b", new HashSet<string>()),
        // Granular roles for the control-plane CRUD/admin surface (least privilege per endpoint):
        // read-only operator (may read, never mutate), game-admin (may mutate its tenant's resources, no
        // platform ops), and platform-admin (full authority + platform-level ops).
        ["dev-operator-a-key"] = new("dev-readonly-a", "tenant-a", new HashSet<string> { CallerPrincipal.OperatorRole }),
        ["dev-gameadmin-a-key"] = new("dev-gameadmin-a", "tenant-a", new HashSet<string> { CallerPrincipal.GameAdminRole }),
        ["dev-gameadmin-b-key"] = new("dev-gameadmin-b", "tenant-b", new HashSet<string> { CallerPrincipal.GameAdminRole }),
        ["dev-admin-key"] = new("dev-admin", "tenant-a", new HashSet<string> { CallerPrincipal.PlatformAdminRole }),
    };
}
else
{
    var configured = builder.Configuration.GetSection("ControlPlane:ApiKeys").Get<List<ApiKeyEntry>>() ?? new();
    if (configured.Count == 0)
    {
        throw new InvalidOperationException(
            "No control-plane API keys are configured (ControlPlane:ApiKeys). Refusing to start " +
            "outside Development with an unauthenticated control plane.");
    }

    controlPlaneApiKeys = configured.ToDictionary(
        e => e.Key ?? throw new InvalidOperationException("A ControlPlane:ApiKeys entry is missing its Key."),
        e => new CallerPrincipal(
            e.CallerId ?? throw new InvalidOperationException($"API key '{e.Key}' is missing CallerId."),
            e.TenantId ?? throw new InvalidOperationException($"API key '{e.Key}' is missing TenantId."),
            (e.Roles ?? new List<string>()).ToHashSet()));
}

builder.Services.AddSingleton<IControlPlaneAuthenticator>(new ApiKeyControlPlaneAuthenticator(controlPlaneApiKeys));
// Durable audit on the Postgres path: each tenant-scoped audited action (and denial) is appended to that
// tenant's own audit_records table, so the audit trail is isolated per tenant exactly like its other data.
// Platform-level actions (no tenant) are held in the in-memory mirror the durable log keeps. In-memory
// otherwise. Same IAuditLog contract either way.
if (string.Equals(builder.Configuration["Persistence:Backend"], "Postgres", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IAuditLog>(sp => new PostgresAuditLog(sp.GetRequiredService<ITenantDbContextFactory>()));
}
else
{
    builder.Services.AddSingleton<IAuditLog, InMemoryAuditLog>();
}

// ---- Clustering: room ownership directory, placement, affinity routing -------
// A fleet must agree on exactly one owner per room or it split-brains (the same room ticking on two
// nodes — see MultiNodeOwnershipScenario). The directory is the source of truth; placement assigns
// owners capacity-aware; the affinity router redirects a connection that landed on a non-owner. The
// backend is config-selected: in-memory for a single node / dev (authoritative for THIS process only),
// Redis for a real fleet — same contracts, like InMemory↔durable snapshot stores.
if (string.Equals(builder.Configuration["Cluster:Backend"], "Redis", StringComparison.OrdinalIgnoreCase))
{
    var redisConnection = builder.Configuration["Cluster:Redis:ConnectionString"]
        ?? throw new InvalidOperationException("Cluster:Backend=Redis requires Cluster:Redis:ConnectionString.");
    var redisOptions = new RedisClusterOptions();
    builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConnection));
    builder.Services.AddSingleton<IRoomDirectory>(sp => new RedisRoomDirectory(sp.GetRequiredService<IConnectionMultiplexer>(), redisOptions));
    builder.Services.AddSingleton<IRoomPlacement>(sp => new RedisRoomPlacement(sp.GetRequiredService<IConnectionMultiplexer>(), fleetNodes, maxRoomsPerNode, redisOptions, reservedHeadroom));
}
else
{
    builder.Services.AddSingleton<IRoomDirectory, InMemoryRoomDirectory>();
    builder.Services.AddSingleton<IRoomPlacement>(sp => new CapacityAwareRoomPlacement(sp.GetRequiredService<IRoomDirectory>(), fleetNodes, maxRoomsPerNode, reservedHeadroom));
}

builder.Services.AddSingleton<IRoomAffinityRouter>(sp => new DirectoryRoomAffinityRouter(sp.GetRequiredService<IRoomDirectory>(), localNode));
// Drains a node cleanly: marks it draining (fleet-visible) and sheds each room it owns onto a live node
// via the same capacity-aware placement, with no room stranded or double-owned (see NodeDrainCoordinator).
builder.Services.AddSingleton<NodeDrainCoordinator>(sp => new NodeDrainCoordinator(
    sp.GetRequiredService<IRoomDirectory>(), sp.GetRequiredService<IRoomPlacement>()));

// Renew this node's room-ownership leases while it serves them, well inside the lease window, so a
// distributed lease never lapses under a live room (which would let another node claim it — split
// brain). For the in-memory directory (no expiry) this is a cheap idempotent no-op. Supervised like
// the other long-running loops.
var leaseRenewalInterval = TimeSpan.FromSeconds(builder.Configuration.GetValue("Cluster:LeaseRenewalSeconds", 10));
builder.Services.AddSingleton<RoomLeaseRenewalService>(sp => new RoomLeaseRenewalService(
    sp.GetRequiredService<RealtimeServer>(),
    sp.GetRequiredService<IRoomDirectory>(),
    localNode,
    leaseRenewalInterval,
    sp.GetRequiredService<ILogger<RoomLeaseRenewalService>>()));
builder.Services.AddSingleton<ISupervisedWorker>(sp => sp.GetRequiredService<RoomLeaseRenewalService>());

// ---- Matchmaking (Wave 5): Open Match-style, OUTSIDE the room runtime --------
// Matchmaking (rank 2) hands off to allocation + token mint through its OWN ports, adapted here at the
// composition root. RoutingRoomAllocator bridges to the real Wave-4 IRoomPlacement (also rank 2) and
// JoinTokenServiceIssuer to the real control-plane JoinTokenService — so Matchmaking never takes a
// sideways rank-2 dependency on Routing (CITADEL0002 stays clean). The TicketRegistry buckets tickets
// by tenant/game/version scope, and the director's atomic ticket-claim makes assignment idempotent.
builder.Services.AddSingleton<IRoomAllocator>(sp => new RoutingRoomAllocator(
    sp.GetRequiredService<InMemoryRoomRegistry>(), sp.GetRequiredService<IRoomPlacement>()));
builder.Services.AddSingleton<IMatchJoinTokenIssuer>(sp => new JoinTokenServiceIssuer(
    sp.GetRequiredService<JoinTokenService>()));
builder.Services.AddSingleton<Evaluator>();
builder.Services.AddSingleton<TicketRegistry>(sp => new TicketRegistry(sp.GetRequiredService<IMonotonicClock>()));
var matchSize = builder.Configuration.GetValue("Matchmaking:MatchSize", 2);
builder.Services.AddSingleton<IMatchFunction>(_ => new FixedSizeMatchFunction(matchSize));
builder.Services.AddSingleton<MatchDirector>(sp => new MatchDirector(
    sp.GetRequiredService<IMatchFunction>(),
    sp.GetRequiredService<Evaluator>(),
    sp.GetRequiredService<IRoomAllocator>(),
    sp.GetRequiredService<IMatchJoinTokenIssuer>()));
// Retains each cycle's assignments by ticket id (TTL-bounded) so a player whose ticket was QUEUED on
// submit and matched by a LATER cycle can still fetch its room+token via GET — closing the
// lost-assignment gap where a 202 Location would otherwise 404 forever after the ticket is removed.
var assignmentTtlSeconds = builder.Configuration.GetValue("Matchmaking:AssignmentTtlSeconds", 300.0);
builder.Services.AddSingleton<AssignmentStore>(sp => new AssignmentStore(
    sp.GetRequiredService<IMonotonicClock>(), assignmentTtlSeconds));

// ---- Readiness contributors (real dependency probes for /ready) -------------
// Each probe is a CHEAP invariant on a hot-path dependency, so /ready proves the node can actually
// do useful work without becoming a DoS vector. Registered as IReadinessCheck so the /ready endpoint
// folds them generically — and so a test can substitute a failing fake to prove /ready flips to 503.
builder.Services.AddSingleton<IReadinessCheck>(sp => new DelegateReadinessCheck("telemetry", () =>
{
    // The aggregating sink the hot path feeds: a successful snapshot proves it can accept telemetry.
    sp.GetRequiredService<AggregatingTelemetrySink>().Snapshot();
    return ReadinessResult.Healthy("telemetry");
}));
builder.Services.AddSingleton<IReadinessCheck>(sp => new DelegateReadinessCheck("tenant-resolver", () =>
{
    // The resolver must be present and answerable; an unknown tenant resolving false is still a
    // healthy resolver (it answered). A throw (resolver wedged) is caught by /ready as not-ready.
    sp.GetRequiredService<ITenantResolver>().TryResolve(new TenantId("__readiness_probe__"), out _);
    return ReadinessResult.Healthy("tenant-resolver");
}));
builder.Services.AddSingleton<IReadinessCheck>(sp => new DelegateReadinessCheck("snapshot-store", () =>
{
    // A read against a probe key proves the store is reachable and answering (the in-memory store
    // returns false for a miss; a durable store would surface an outage as a throw → not-ready).
    sp.GetRequiredService<ISnapshotStore<RoomKey, RoomSnapshot>>()
        .TryGetLatest(new RoomKey(new TenantId("__readiness_probe__"), new RoomId("__probe__")), out _);
    return ReadinessResult.Healthy("snapshot-store");
}));
// When a distributed room directory is configured, the node is not ready until it can reach it
// (otherwise it would accept connections it cannot place or fence — split brain). No-op for the
// in-memory backend (no multiplexer registered).
builder.Services.AddSingleton<IReadinessCheck>(sp => new DelegateReadinessCheck("room-directory", () =>
{
    var multiplexer = sp.GetService<IConnectionMultiplexer>();
    return multiplexer is null || multiplexer.IsConnected
        ? ReadinessResult.Healthy("room-directory")
        : ReadinessResult.Unhealthy("room-directory", "room directory (redis) is not connected");
}));

var app = builder.Build();

// Cap a single reassembled realtime message so one connection cannot force the server to
// buffer without bound (a single-connection OOM). Configurable; defaults to a size far above
// any legitimate command frame.
var maxFrameBytes = app.Configuration.GetValue("Realtime:MaxFrameBytes", AspNetRealtimeChannel.DefaultMaxMessageBytes);

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets();

// ============================================================================
// Control-plane HTTP API. JSON DTOs; session lifecycle only; no gameplay logic.
// ============================================================================
// Liveness: a cheap "the process is up" probe. It must stay trivial — it says nothing about
// whether dependencies are healthy (that is /ready's job) and must never block on one.
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
// Readiness PROVES the node can do useful work: it runs every registered IReadinessCheck
// contributor (telemetry sink, tenant resolver, snapshot store, and the room directory when a
// distributed backend is configured) and flips to 503 the moment ANY dependency is not ready, so an
// orchestrator stops routing traffic to a node that would only fail requests. Each contributor is
// CHEAP (a fast invariant, not a scan), so readiness cannot itself become a DoS vector. A throwing
// contributor is treated as not-ready (fail-closed) rather than crashing the probe.
app.MapGet("/ready", (IEnumerable<IReadinessCheck> checks) =>
{
    var results = new List<ReadinessResult>();
    foreach (var check in checks)
    {
        try
        {
            results.Add(check.Check());
        }
        catch (Exception ex)
        {
            results.Add(ReadinessResult.Unhealthy(check.Name, $"probe threw: {ex.GetType().Name}"));
        }
    }

    var dependencies = results.Select(r => new { name = r.Name, ready = r.Ready, detail = r.Detail }).ToArray();
    if (results.Any(r => !r.Ready))
    {
        return Results.Json(
            new { status = "not-ready", dependencies },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Ok(new { status = "ready", dependencies });
});
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
api.MapGet("/games", (IGameRegistry catalog) => Results.Ok(catalog.List()));

api.MapGet("/games/{gameId}", (string gameId, IGameRegistry catalog) =>
    catalog.TryGet(gameId, out var game)
        ? Results.Ok(game)
        : Results.NotFound(new ApiError("GameNotFound", $"Game '{gameId}' was not found.")));

// ===========================================================================
// Control-plane CRUD (Wave 7). EVERY mutation is least-privilege gated and audited (success AND denial);
// the registries are runtime-mutable so provisioning a tenant/game/version is an API call, not a code
// change. Tenant enumeration is authorization-filtered so no caller can list another tenant's resources.
// ===========================================================================

// --- Tenant CRUD: platform-level (provisioning a tenant spans the fleet → platform-admin only) ---
api.MapPost("/tenants", (HttpContext ctx, CreateTenantRequest request, ITenantRegistry tenants, IAuditLog audit, IClock clock) =>
{
    if (ForbidPlatform(ctx, "create-tenant", request.TenantId, audit, clock) is { } denied)
    {
        return denied;
    }

    if (!tenants.TryCreate(new TenantRecordDto(request.TenantId, request.DisplayName)))
    {
        return Results.Conflict(new ApiError("TenantExists", $"Tenant '{request.TenantId}' already exists."));
    }

    Audit(ctx, "create-tenant", request.TenantId, audit, clock);
    return Results.Created($"/api/v1/tenants/{request.TenantId}", new TenantRecordDto(request.TenantId, request.DisplayName));
});

// List tenants: a platform admin sees all; a tenant-scoped caller sees ONLY its own tenant (no
// cross-tenant enumeration via the list). A read-only operator may read this; a no-role caller cannot.
api.MapGet("/tenants", (HttpContext ctx, ITenantRegistry tenants) =>
{
    var caller = (CallerPrincipal)ctx.Items["caller"]!;
    var visible = tenants.List().Where(t => caller.CanActFor(t.TenantId)).ToList();
    return Results.Ok(visible);
});

api.MapGet("/tenants/{tenantId}", (HttpContext ctx, string tenantId, ITenantRegistry tenants, IAuditLog audit, IClock clock) =>
{
    // A caller may not even probe for the existence of another tenant: authorize BEFORE the lookup so a
    // 403 (not a 404) is returned regardless of whether the tenant exists — no enumeration oracle.
    if (Forbid(ctx, tenantId, "read-tenant", tenantId, audit, clock) is { } denied)
    {
        return denied;
    }

    return tenants.TryGet(tenantId, out var tenant)
        ? Results.Ok(tenant)
        : Results.NotFound(new ApiError("TenantNotFound", $"Tenant '{tenantId}' was not found."));
});

api.MapDelete("/tenants/{tenantId}", (HttpContext ctx, string tenantId, ITenantRegistry tenants, IAuditLog audit, IClock clock) =>
{
    if (ForbidPlatform(ctx, "delete-tenant", tenantId, audit, clock) is { } denied)
    {
        return denied;
    }

    if (!tenants.TryDelete(tenantId))
    {
        return Results.NotFound(new ApiError("TenantNotFound", $"Tenant '{tenantId}' was not found."));
    }

    Audit(ctx, "delete-tenant", tenantId, audit, clock);
    return Results.NoContent();
});

// --- Game CRUD: tenant-scoped mutation (game-admin of the owning tenant, or platform-admin) ---
api.MapPost("/games", (HttpContext ctx, CreateGameRequest request, IGameRegistry catalog, ITenantRegistry tenants, IAuditLog audit, IClock clock) =>
{
    var target = $"{request.TenantId}/{request.GameId}";
    if (ForbidMutation(ctx, request.TenantId, "create-game", target, audit, clock) is { } denied)
    {
        return denied;
    }

    if (!tenants.TryGet(request.TenantId, out _))
    {
        return Results.NotFound(new ApiError("TenantNotFound", $"Tenant '{request.TenantId}' was not found."));
    }

    var detail = new GameDetail(request.GameId, request.Name, request.Description, request.ProtocolVersion);
    if (!catalog.TryCreateGame(request.TenantId, detail))
    {
        return Results.Conflict(new ApiError("GameExists", $"Game '{request.GameId}' already exists."));
    }

    Audit(ctx, "create-game", target, audit, clock, request.TenantId);
    return Results.Created($"/api/v1/games/{request.GameId}", detail);
});

// Register a new schema version of an existing game. Tenant-scoped mutation: the caller must be authorized
// for the game's owning tenant. (Game ids are platform-unique; the owning tenant is resolved from the
// catalog so a caller cannot register a version for a game it does not own.)
api.MapPost("/games/{gameId}/versions", (HttpContext ctx, string gameId, CreateGameVersionRequest request, IGameRegistry catalog, IAuditLog audit, IClock clock) =>
{
    // Least-privilege: must be able to mutate, AND (for a non-platform caller) be authorized for the
    // game's OWNING tenant — never merely the caller's own tenant, or a game-admin of one tenant could
    // version another tenant's game by id. Resolve ownership from the registry, the source of truth.
    if (!catalog.TryGetOwningTenant(gameId, out var owningTenant))
    {
        return Results.NotFound(new ApiError("GameNotFound", $"Game '{gameId}' was not found."));
    }

    var target = $"{owningTenant}/{gameId}@v{request.SchemaVersion}";
    if (ForbidMutation(ctx, owningTenant, "create-game-version", target, audit, clock) is { } denied)
    {
        return denied;
    }

    if (!catalog.TryGet(gameId, out _))
    {
        return Results.NotFound(new ApiError("GameNotFound", $"Game '{gameId}' was not found."));
    }

    if (!catalog.TryCreateVersion(new GameVersionDto(gameId, request.SchemaVersion, request.Notes)))
    {
        return Results.Conflict(new ApiError("GameVersionExists", $"Version {request.SchemaVersion} of '{gameId}' already exists."));
    }

    Audit(ctx, "create-game-version", target, audit, clock, owningTenant);
    return Results.Created($"/api/v1/games/{gameId}/versions/{request.SchemaVersion}", new GameVersionDto(gameId, request.SchemaVersion, request.Notes));
});

api.MapGet("/games/{gameId}/versions", (string gameId, IGameRegistry catalog) =>
    catalog.TryGet(gameId, out _)
        ? Results.Ok(catalog.ListVersions(gameId))
        : Results.NotFound(new ApiError("GameNotFound", $"Game '{gameId}' was not found.")));

api.MapDelete("/games/{gameId}", (HttpContext ctx, string gameId, IGameRegistry catalog, IAuditLog audit, IClock clock) =>
{
    // A game is tenant-scoped data: authorize the DELETE against the game's OWNING tenant (resolved from
    // the registry), not the caller's own tenant — otherwise a game-admin of one tenant could delete
    // another tenant's game by id (and, on the durable backend, route the delete into the victim's database).
    if (!catalog.TryGetOwningTenant(gameId, out var owningTenant))
    {
        return Results.NotFound(new ApiError("GameNotFound", $"Game '{gameId}' was not found."));
    }

    if (ForbidMutation(ctx, owningTenant, "delete-game", gameId, audit, clock) is { } denied)
    {
        return denied;
    }

    if (!catalog.TryDeleteGame(gameId))
    {
        return Results.NotFound(new ApiError("GameNotFound", $"Game '{gameId}' was not found."));
    }

    Audit(ctx, "delete-game", gameId, audit, clock, owningTenant);
    return Results.NoContent();
});

api.MapPost("/rooms", (HttpContext ctx, CreateRoomRequest request, IGameRegistry catalog, InMemoryRoomRegistry rooms, IRoomPlacement placement, IAuditLog audit, IClock clock) =>
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

    // Assign the room a single owning node across the cluster. A full cluster sheds the request
    // cleanly (503) rather than placing a room nothing can own. The owner is surfaced in a header so a
    // client can connect straight to it (the connect path also redirects a mis-routed client).
    var placed = placement.Place(new RoomKey(new TenantId(room.TenantId), new RoomId(room.RoomId)));
    if (!placed.IsPlaced)
    {
        return Results.Json(
            new ApiError("ClusterAtCapacity", "No node has spare capacity for a new room."),
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    ctx.Response.Headers["X-Citadel-Owner-Node"] = placed.Owner.Value;
    Audit(ctx, "create-room", $"{room.TenantId}/{room.RoomId}@{placed.Owner.Value}", audit, clock, room.TenantId);
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
    (HttpContext ctx, string roomId, CreateJoinTokenRequest request, InMemoryRoomRegistry rooms, JoinTokenService tokens, IRoomDirectory directory, IAuditLog audit, IClock clock) =>
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

    // Tell the client which node owns the room so it can connect directly to the owner (the connect
    // path still redirects if it lands elsewhere). Surfaced as a header to keep the token body stable.
    if (directory.TryGetOwner(new RoomKey(new TenantId(room.TenantId), new RoomId(room.RoomId)), out var owner))
    {
        ctx.Response.Headers["X-Citadel-Owner-Node"] = owner.Value;
    }

    Audit(ctx, "mint-join-token", target, audit, clock, room.TenantId);
    return Results.Created($"/realtime/v1/connect?joinToken={token.Token}", token);
});

// Matchmaking (Wave 5): submit a scoped ticket and run an assignment cycle. The scope is the caller's
// tenant + the requested game + game version — baked in here, so a caller can never submit a ticket
// for another tenant (Forbid enforces tenant access) and matchmaking only ever matches within scope.
api.MapPost("/matchmaking/tickets",
    (HttpContext ctx, MatchmakingTicketRequest request, TicketRegistry registry, MatchDirector director, AssignmentStore assignments, IAuditLog audit, IClock clock) =>
{
    if (Forbid(ctx, request.TenantId, "submit-match-ticket", $"{request.TenantId}/{request.GameId}/{request.PlayerId}", audit, clock) is { } denied)
    {
        return denied;
    }

    var scope = new MatchScope(new TenantId(request.TenantId), new GameId(request.GameId), request.GameVersion);
    var ticketId = $"{request.PlayerId}:{Guid.NewGuid():N}";
    registry.Submit(ticketId, scope, new PlayerId(request.PlayerId), request.Skill);

    // Run one cycle over THIS scope's tickets only (the registry buckets by scope — no cross-tenant leakage).
    var pool = new Pool("default", scope);
    var cycle = director.Cycle(pool, registry.ActiveTickets(scope));

    // Drop the tickets that were assigned this cycle so they are not re-matched, and retain each player's
    // assignment so a player matched in THIS cycle on an EARLIER submission can still fetch it by ticket id.
    foreach (var match in cycle)
    {
        registry.Remove(scope, match.Players.Select(p => p.TicketId));
        foreach (var player in match.Players)
        {
            assignments.Record(player);
        }
    }

    Audit(ctx, "submit-match-ticket", $"{scope}/{request.PlayerId}", audit, clock, request.TenantId);

    // If THIS player's ticket got assigned, hand back its room + token; otherwise it is queued and the
    // caller polls the (now real) fetch endpoint at the returned Location.
    var mine = cycle
        .SelectMany(m => m.Players)
        .FirstOrDefault(p => p.TicketId == ticketId);

    return mine is null
        ? Results.Accepted($"/api/v1/matchmaking/tickets/{ticketId}", new { ticketId, status = "queued" })
        : Results.Ok(new JoinTokenContract(mine.JoinToken, scope.TenantId.Value, scope.GameId.Value, mine.RoomId.Value, mine.PlayerId.Value));
});

// Fetch a ticket's assignment (the Location returned by a queued 202). Returns the room+token once the
// ticket has been matched — by THIS submission's cycle or a LATER one — or 404 while still queued (or once
// the assignment's retention window has elapsed). The ticket id embeds the player id ("player:guid"), so a
// caller is authorized for the tenant whose player it names; a caller may not fetch another tenant's
// assignment. This closes the lost-assignment gap: a matched-but-not-self-submitting player can now claim
// its token instead of polling a 404 forever.
api.MapGet("/matchmaking/tickets/{ticketId}",
    (HttpContext ctx, string ticketId, AssignmentStore assignments, IAuditLog audit, IClock clock) =>
{
    if (!assignments.TryGet(ticketId, out var assignment))
    {
        // Still queued or expired: not an error, just not yet assignable. No existence oracle either way.
        return Results.NotFound(new ApiError("NotAssigned", "Ticket is not assigned (still queued, unknown, or expired)."));
    }

    // The assignment carries its own scope; authorize the caller for that tenant before revealing the token.
    if (Forbid(ctx, assignment.Scope.TenantId.Value, "fetch-match-assignment", ticketId, audit, clock) is { } denied)
    {
        return denied;
    }

    Audit(ctx, "fetch-match-assignment", ticketId, audit, clock, assignment.Scope.TenantId.Value);
    return Results.Ok(new JoinTokenContract(
        assignment.JoinToken, assignment.Scope.TenantId.Value, assignment.Scope.GameId.Value,
        assignment.RoomId.Value, assignment.PlayerId.Value));
});

api.MapPost("/sessions", (HttpContext ctx, CreateSessionRequest request, InMemorySessionRegistry sessions, IAuditLog audit, IClock clock) =>
{
    if (Forbid(ctx, request.TenantId, "create-session", $"{request.TenantId}/{request.PlayerId}", audit, clock) is { } denied)
    {
        return denied;
    }

    var session = sessions.Create(request);
    Audit(ctx, "create-session", $"{request.TenantId}/{session.SessionId}", audit, clock, request.TenantId);
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
    Audit(ctx, "delete-session", $"{session.TenantId}/{sessionId}", audit, clock, session.TenantId);
    return Results.NoContent();
});

// ============================================================================
// Admin / operator APIs: authenticated, tenant-authorized, audited, and strictly
// READ-ONLY. "Drop in and see what a session is seeing" for live debugging — they
// observe authoritative state and never mutate it outside the room pathway.
// ============================================================================
api.MapGet("/admin/rooms", (HttpContext ctx, RealtimeServer server, RoomScopedMetrics roomMetrics, IAuditLog audit, IClock clock) =>
{
    var caller = (CallerPrincipal)ctx.Items["caller"]!;

    var rooms = new List<object>();
    foreach (var key in server.ActiveRooms)
    {
        // A caller sees only rooms in tenants it may act for (platform admin sees all).
        if (!caller.CanActFor(key.TenantId.Value) || !server.TryObserveRoom(key, out var observation))
        {
            continue;
        }

        rooms.Add(new
        {
            tenantId = observation.TenantId,
            roomId = observation.RoomId,
            tick = observation.Tick,
            subscriberCount = observation.SubscriberCount,
            entityCount = observation.Entities.Count,
        });
    }

    // The hottest rooms by tick cost (gap #6's per-room telemetry), authorization-filtered.
    var hottest = roomMetrics.Hottest(20)
        .Where(s => caller.CanActFor(TenantOf(s.Room)))
        .Select(s => new { room = s.Room, meanMs = Math.Round(s.MeanMs, 2), maxMs = Math.Round(s.MaxMs, 2) });

    Audit(ctx, "admin-list-rooms", "*", audit, clock);
    return Results.Ok(new { rooms, hottest });
});

// Drain a node: a PLATFORM-ADMIN operation (it acts on fleet topology, not one tenant's data). The node
// is marked draining (refuses new allocations, fleet-visible via the directory) and every room it owns is
// re-placed onto a live node — no room stranded or double-owned (the move is atomic in the directory).
// Authenticated by the group filter; restricted to platform admins; audited. Rooms the cluster has no
// live capacity to take are reported (clusterAtCapacity) and left owned by the draining node, not lost.
api.MapPost("/admin/nodes/{nodeId}/drain", (HttpContext ctx, string nodeId, NodeDrainCoordinator drainer, IAuditLog audit, IClock clock) =>
{
    var caller = (CallerPrincipal)ctx.Items["caller"]!;
    if (!caller.IsPlatformAdmin)
    {
        audit.Record(new AuditRecord(caller.CallerId, "drain-node", nodeId, clock.UtcNow, "denied"));
        return Results.Json(
            new ApiError("Forbidden", "Draining a node is a platform-admin operation."),
            statusCode: StatusCodes.Status403Forbidden);
    }

    var outcomes = drainer.Drain(new NodeId(nodeId));
    var shed = outcomes.Where(o => o.Result.IsPlaced)
        .Select(o => new { room = $"{o.Room.TenantId.Value}/{o.Room.RoomId.Value}", newOwner = o.Result.Owner.Value })
        .ToArray();
    var stranded = outcomes.Where(o => !o.Result.IsPlaced)
        .Select(o => $"{o.Room.TenantId.Value}/{o.Room.RoomId.Value}")
        .ToArray();

    Audit(ctx, "drain-node", nodeId, audit, clock);
    return Results.Ok(new
    {
        node = nodeId,
        draining = true,
        shedCount = shed.Length,
        shed,
        // Rooms left on the draining node because no live node had capacity — surfaced, not dropped.
        clusterAtCapacity = stranded,
    });
});

// Administratively TERMINATE a live room. This is a tenant-scoped MUTATION (game-admin of the room's
// tenant, or platform-admin — never a read-only operator). It routes through the AUTHORITATIVE pathway
// (RealtimeServer.TerminateRoom → the room owner's OnTerminate, the same teardown a reap performs), NOT a
// side mutation of room state: after it returns the room is gone from ActiveRooms with no orphan state.
// Tenant-authorized BEFORE touching the room so it cannot terminate (or even probe) another tenant's room.
api.MapPost("/admin/rooms/{tenantId}/{roomId}/terminate", (HttpContext ctx, string tenantId, string roomId, RealtimeServer server, IAuditLog audit, IClock clock) =>
{
    var target = $"{tenantId}/{roomId}";
    if (ForbidMutation(ctx, tenantId, "terminate-room", target, audit, clock) is { } denied)
    {
        return denied;
    }

    var key = new RoomKey(new TenantId(tenantId), new RoomId(roomId));
    if (!server.TerminateRoom(key, "admin-terminate"))
    {
        return Results.NotFound(new ApiError("RoomNotFound", $"Room '{target}' is not active."));
    }

    Audit(ctx, "terminate-room", target, audit, clock, tenantId);
    return Results.Ok(new { tenantId, roomId, terminated = true });
});

// Read the realtime admission ceilings (capacity/limits). Read-only operators and above may read.
api.MapGet("/admin/limits", (HttpContext ctx, RealtimeServer server, IAuditLog audit, IClock clock) =>
{
    var caller = (CallerPrincipal)ctx.Items["caller"]!;
    // Capacity is a platform-level view (fleet ceilings, not one tenant's data): platform admins only.
    if (!caller.IsPlatformAdmin)
    {
        audit.Record(new AuditRecord(caller.CallerId, "read-limits", "*", clock.UtcNow, "denied"));
        return Results.Json(new ApiError("Forbidden", "Reading capacity limits is a platform-admin operation."),
            statusCode: StatusCodes.Status403Forbidden);
    }

    var a = server.Admission;
    return Results.Ok(new AdmissionLimitsContract(a.MaxConnections, a.MaxConnectionsPerTenant, a.MaxRooms, a.MaxRoomsPerTenant));
});

// Update the admission ceilings at runtime (platform-admin). Audited. Does not evict existing
// connections/rooms — it gates only NEW admissions until live counts fall under the new ceilings.
api.MapPut("/admin/limits", (HttpContext ctx, AdmissionLimitsContract request, RealtimeServer server, IAuditLog audit, IClock clock) =>
{
    if (ForbidPlatform(ctx, "update-limits", "*", audit, clock) is { } denied)
    {
        return denied;
    }

    // Reject degenerate ceilings BEFORE applying them. A zero or negative ceiling is not a meaningful
    // capacity: e.g. MaxConnections <= 0 would make the admission counter compare `count >= 0` true on
    // the very first connect and silently refuse ALL traffic — a self-inflicted outage from a fat-finger.
    // A ceiling must be at least 1 (use int.MaxValue for "unbounded"). Audited as a rejected attempt.
    if (request.MaxConnections < 1 || request.MaxConnectionsPerTenant < 1
        || request.MaxRooms < 1 || request.MaxRoomsPerTenant < 1)
    {
        audit.Record(new AuditRecord(
            ((CallerPrincipal)ctx.Items["caller"]!).CallerId, "update-limits", "*", clock.UtcNow, "rejected-invalid"));
        return Results.Json(
            new ApiError("InvalidLimits", "Every admission ceiling must be >= 1 (use a large value such as 2147483647 for unbounded)."),
            statusCode: StatusCodes.Status400BadRequest);
    }

    server.UpdateAdmission(new AdmissionPolicy(
        request.MaxConnections, request.MaxConnectionsPerTenant, request.MaxRooms, request.MaxRoomsPerTenant));

    Audit(ctx, "update-limits", "*", audit, clock);
    var a = server.Admission;
    return Results.Ok(new AdmissionLimitsContract(a.MaxConnections, a.MaxConnectionsPerTenant, a.MaxRooms, a.MaxRoomsPerTenant));
});

// Operational metrics / dashboard. Platform admins see the fleet view; a tenant-scoped caller sees only
// its own tenant's attribution (no cross-tenant metric leakage). Read-only operators and above.
api.MapGet("/admin/metrics", (HttpContext ctx, RealtimeServer server, AggregatingTelemetrySink telemetry, TenantScopedMetrics tenantMetrics, IAuditLog audit, IClock clock) =>
{
    var caller = (CallerPrincipal)ctx.Items["caller"]!;
    var snapshot = telemetry.Snapshot();
    // Per-tenant inbound attribution, authorization-filtered: a tenant-scoped caller sees ONLY its own
    // tenant's metric, never another tenant's (no cross-tenant metric enumeration).
    var perTenant = tenantMetrics.Ranked(TelemetryMetrics.MessagesIn, 100)
        .Where(t => caller.CanActFor(t.Tenant))
        .Select(t => new { tenantId = t.Tenant, messagesIn = t.Count });

    Audit(ctx, "admin-metrics", caller.IsPlatformAdmin ? "*" : caller.TenantId, audit, clock);
    return Results.Ok(new
    {
        activeConnections = server.ActiveConnectionCount,
        activeRooms = server.ActiveRooms.Count,
        degradationLevel = server.DegradationLevel.ToString(),
        // The global aggregate counters are a platform view; a tenant-scoped caller sees only its slice.
        global = caller.IsPlatformAdmin ? (object)snapshot : "platform-admin only",
        perTenant,
    });
});

api.MapGet("/admin/rooms/{tenantId}/{roomId}", (HttpContext ctx, string tenantId, string roomId, RealtimeServer server, IAuditLog audit, IClock clock) =>
{
    if (Forbid(ctx, tenantId, "admin-observe-room", $"{tenantId}/{roomId}", audit, clock) is { } denied)
    {
        return denied;
    }

    var key = new RoomKey(new TenantId(tenantId), new RoomId(roomId));
    if (!server.TryObserveRoom(key, out var observation))
    {
        return Results.NotFound(new ApiError("RoomNotFound", $"Room '{tenantId}/{roomId}' is not active."));
    }

    Audit(ctx, "admin-observe-room", $"{tenantId}/{roomId}", audit, clock, tenantId);
    return Results.Ok(observation);
});

// Read-only REPLAY: rebuild a room's authoritative state from its DURABLE snapshot + post-snapshot
// event log and return the projected entity state, WITHOUT registering a live room or mutating
// anything. This is the operator's "what state would this room recover to" view — the same recovery
// path a restarted node uses, exposed for diagnostics. Authenticated (group filter), tenant-authorized
// (Forbid), and audited. Strictly read-only: it never goes through (or around) the authoritative
// pathway, so it cannot be a backdoor to mutate game state.
api.MapGet("/admin/rooms/{tenantId}/{roomId}/replay",
    (HttpContext ctx, string tenantId, string roomId, string? gameId,
        ISnapshotStore<RoomKey, RoomSnapshot> snapshots, IEventLog<RoomKey, RoomEvent> events,
        ITelemetrySink telemetry, IAuditLog audit, IClock clock) =>
{
    if (Forbid(ctx, tenantId, "admin-replay-room", $"{tenantId}/{roomId}", audit, clock) is { } denied)
    {
        return denied;
    }

    var key = new RoomKey(new TenantId(tenantId), new RoomId(roomId));

    // Recovery reads ONLY this key's tenant/room data, so it can never observe another tenant's
    // snapshot or events. The game to replay under is supplied by the operator (?gameId=), defaulting
    // to the platform's default game; GameFor selects the matching deterministic simulation.
    if (!snapshots.TryGetLatest(key, out var checkpoint))
    {
        return Results.NotFound(new ApiError("NoCheckpoint", $"Room '{tenantId}/{roomId}' has no durable snapshot to replay."));
    }

    var recovery = new RoomRecoveryService(
        snapshots, events,
        (rid, gid) => new GameRoom(rid, GameFor(gid), new LogicalSimulationClock(), new SeededRandomSource()),
        telemetry);

    var result = recovery.Restore(key, new GameId(gameId ?? "demo"), MissingSnapshotPolicy.Fail);
    if (result.Outcome != RoomRecoveryOutcome.RestoredFromSnapshot || result.Room is null)
    {
        return Results.Json(
            new ApiError("ReplayFailed", "The room could not be replayed from its durable artifacts (corrupt or incompatible recovery data)."),
            statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    Audit(ctx, "admin-replay-room", $"{tenantId}/{roomId}", audit, clock, tenantId);
    return Results.Ok(new
    {
        tenantId,
        roomId,
        restoredTick = result.RestoredTick,
        replayedEventCount = result.ReplayedEventCount,
        seed = checkpoint.Seed,
        gameSchemaVersion = checkpoint.GameSchemaVersion,
        entities = result.Room.Project().Select(e => new { id = e.Id.Value, version = e.Version }),
    });
});

// Live "drop in" stream: a read-only SSE feed of the room observation, ~4 Hz, until the
// admin disconnects, the room goes away, or the host shuts down. The auth filter has
// already authenticated the caller; we authorize the tenant and audit the attach.
api.MapGet("/admin/rooms/{tenantId}/{roomId}/observe",
    async (HttpContext ctx, string tenantId, string roomId, RealtimeServer server, IAuditLog audit, IClock clock, IHostApplicationLifetime lifetime, CancellationToken token) =>
{
    if (Forbid(ctx, tenantId, "admin-observe-stream", $"{tenantId}/{roomId}", audit, clock) is { } denied)
    {
        return denied;
    }

    Audit(ctx, "admin-observe-stream", $"{tenantId}/{roomId}", audit, clock, tenantId);

    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";

    var key = new RoomKey(new TenantId(tenantId), new RoomId(roomId));
    var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
    using var streamLifetime = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.ApplicationStopping);
    var streamToken = streamLifetime.Token;
    var streamTick = 0L;

    try
    {
        while (await timer.WaitForNextTickAsync(streamToken))
        {
            if (!server.TryObserveRoom(key, out var observation))
            {
                await ctx.Response.WriteAsync("event: gone\ndata: {}\n\n", streamToken);
                await ctx.Response.Body.FlushAsync(streamToken);
                break;
            }

            // This admin "drop-in" feed is OPTIONAL server-push (a spectator/observer view, not an
            // authoritative client). The degradation ladder's first, cheapest rung thins it under
            // overload (ReduceSpectatorSnapshots+ halves the rate) so fan-out budget is reclaimed before
            // any authoritative work is touched; the timer still ticks so the stream stays alive.
            if (!server.ShouldEmitSpectatorSnapshot(streamTick++))
            {
                continue;
            }

            await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(observation, json)}\n\n", streamToken);
            await ctx.Response.Body.FlushAsync(streamToken);
        }
    }
    catch (OperationCanceledException)
    {
        // The admin closed the stream, or the host is shutting down.
    }

    return Results.Empty;
});

// ============================================================================
// Realtime gameplay transport: binary protobuf only. Requires a join token.
// ============================================================================
app.Map("/realtime/v1/connect", branch => branch.Run(async context =>
{
    var joinToken = context.Request.Query["joinToken"].ToString();
    var verification = context.RequestServices.GetRequiredService<IJoinTokenVerifier>().Verify(joinToken);
    if (!verification.IsOk)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new ApiError("Unauthorized", "Missing or invalid joinToken."));
        return;
    }

    // Room affinity: this connection must be handled by the node that owns the room. A connection that
    // landed on a non-owner is redirected (409 + the owner's address) before any socket is accepted —
    // this is what stops a non-owner from standing up a second authoritative copy of the room (split
    // brain). Fail-closed: if the directory is unavailable we reject (503) rather than serve unclaimed.
    var claims = verification.Claims!;
    var roomKey = new RoomKey(new TenantId(claims.TenantId), new RoomId(claims.RoomId));

    var placement = RoomPlacementResult.ClusterAtCapacity;
    try
    {
        // Fast path: a LIVE node already owns it and it's not us → redirect without a write. A dead /
        // unknown owner falls through to placement, which validates ownership against the live fleet.
        var decision = context.RequestServices.GetRequiredService<IRoomAffinityRouter>().Resolve(roomKey);
        if (decision.Kind == RouteKind.Redirect && fleetNodes.Contains(decision.Owner))
        {
            await WriteWrongNodeAsync(context, decision.Owner);
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new ApiError("MalformedFrame", "A WebSocket upgrade is required."));
            return;
        }

        // Secure ownership through PLACEMENT, not a raw claim, so the connect path is capacity-aware:
        // it returns the room's existing (live) owner, assigns a capacity-checked one, or reports full.
        placement = context.RequestServices.GetRequiredService<IRoomPlacement>().Place(roomKey);
    }
    catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new ApiError("DirectoryUnavailable", "Room directory is unavailable; retry shortly."));
        return;
    }

    if (!placement.IsPlaced)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new ApiError("ClusterAtCapacity", "No node has capacity for this room; retry shortly."));
        return;
    }

    if (!placement.Owner.Equals(localNode))
    {
        // Placement assigned (or confirmed) another node as owner — redirect there.
        await WriteWrongNodeAsync(context, placement.Owner);
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var transport = new ProtobufRealtimeTransport(
        new AspNetRealtimeChannel(socket, maxFrameBytes),
        context.RequestServices.GetRequiredService<RealtimeProtobufCodec>(),
        context.RequestServices.GetRequiredService<RealtimeEnvelopeMapper>(),
        new ConnectionId(Guid.NewGuid().ToString("n")));

    // Link the connection's lifetime to app shutdown: on Ctrl+C the receive loop must end
    // promptly. RequestAborted alone does not fire on a graceful stop, so an idle WebSocket
    // sitting in ReceiveAsync would otherwise hold the process open until the shutdown
    // timeout forcibly aborts it.
    var lifetime = context.RequestServices.GetRequiredService<IHostApplicationLifetime>();
    using var connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(
        context.RequestAborted, lifetime.ApplicationStopping);

    // The verified claims are authoritative for this connection's identity.
    await context.RequestServices.GetRequiredService<RealtimeServer>()
        .HandleConnectionAsync(transport, verification.Claims!, connectionLifetime.Token);
}));

// Dev/debug only: a JSON realtime codec for browser exploration. NOT the contract.
// Gated by BOTH the environment and an explicit opt-in flag (defaults on in Development):
// the AND with IsDevelopment is the hard guarantee that a production host can never expose
// /ws (which trusts query-string identity with no token) or /sim/telemetry (which streams
// aggregate telemetry), even if the flag is set. The flag lets a developer disable them
// locally to exercise the production posture.
var devEndpointsEnabled = app.Environment.IsDevelopment()
    && app.Configuration.GetValue("Realtime:EnableDevEndpoints", true);
if (devEndpointsEnabled)
{
    // Live aggregate telemetry as Server-Sent Events for the simulation console
    // (wwwroot/sim.html). Read-only: it observes the same AggregatingTelemetrySink the
    // hot path feeds and emits one TelemetryRates frame per second via the SAME interval
    // computation the periodic log flush uses. Dev-only, like /ws.
    app.MapGet("/sim/telemetry", async (HttpContext context, AggregatingTelemetrySink sink, IHostApplicationLifetime lifetime, CancellationToken ct) =>
    {
        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no"; // disable proxy buffering of the stream

        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var previous = sink.Snapshot();
        var previousWall = Stopwatch.GetTimestamp();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        // End the stream on app shutdown too, not only when the browser disconnects, so the
        // SSE loop never holds the process open during a graceful stop.
        using var streamLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.ApplicationStopping);
        ct = streamLifetime.Token;

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var now = sink.Snapshot();
                var wall = Stopwatch.GetTimestamp();
                var seconds = Stopwatch.GetElapsedTime(previousWall, wall).TotalSeconds;
                var rates = TelemetryRates.Between(previous, now, seconds);

                await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(rates, json)}\n\n", ct);
                await context.Response.Body.FlushAsync(ct);

                previous = now;
                previousWall = wall;
            }
        }
        catch (OperationCanceledException)
        {
            // The browser closed the EventSource; end the stream cleanly.
        }
    });

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

        // Same shutdown-linked lifetime as the production endpoint (see /realtime/v1/connect).
        var lifetime = context.RequestServices.GetRequiredService<IHostApplicationLifetime>();
        using var connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted, lifetime.ApplicationStopping);

        await context.RequestServices.GetRequiredService<RealtimeServer>()
            .HandleConnectionAsync(transport, devPrincipal, connectionLifetime.Token);
    }));
}

app.Run();

// ---- Realtime affinity helper -----------------------------------------------
// A connection that reached a node which does not own the room is told, with 409, the owner's
// address to reconnect to. No socket is accepted; the body carries the owner so the client can retarget.
static async Task WriteWrongNodeAsync(HttpContext context, NodeId owner)
{
    context.Response.StatusCode = StatusCodes.Status409Conflict;
    await context.Response.WriteAsJsonAsync(new
    {
        error = "WrongNode",
        message = "This room is owned by another node; reconnect to its owner.",
        ownerNode = owner.Value,
    });
}

// ---- Control-plane authorization + audit helpers ----------------------------
// The group filter has already authenticated the caller into HttpContext.Items.

// Returns a 403 result (and audits the denial) when the caller may not act for the
// given tenant; returns null when the caller is authorized. The denial is audited and tenant-scoped so
// it lands in the right tenant's durable trail — audit completeness covers denials, not just successes.
static IResult? Forbid(HttpContext ctx, string tenantId, string action, string target, IAuditLog audit, IClock clock)
{
    var caller = (CallerPrincipal)ctx.Items["caller"]!;
    if (caller.CanActFor(tenantId))
    {
        return null;
    }

    audit.Record(new AuditRecord(caller.CallerId, action, target, clock.UtcNow, "denied", tenantId));
    return Results.Json(
        new ApiError("Forbidden", $"Caller is not authorized for tenant '{tenantId}'."),
        statusCode: StatusCodes.Status403Forbidden);
}

// Enforces, in order: (1) the caller may MUTATE tenant resources at all (game-admin or platform-admin —
// a read-only operator or no-role caller is denied), then (2) tenant authority (CanActFor). Returns a
// 403 (audited as a denial) on the first failed check, or null when authorized. This is the least-
// privilege gate every tenant-scoped MUTATION endpoint runs, so a read-only role can never write.
static IResult? ForbidMutation(HttpContext ctx, string tenantId, string action, string target, IAuditLog audit, IClock clock)
{
    var caller = (CallerPrincipal)ctx.Items["caller"]!;
    if (!caller.CanMutateTenantResources)
    {
        audit.Record(new AuditRecord(caller.CallerId, action, target, clock.UtcNow, "denied", tenantId));
        return Results.Json(
            new ApiError("Forbidden", "Caller does not hold a role that may mutate resources."),
            statusCode: StatusCodes.Status403Forbidden);
    }

    return Forbid(ctx, tenantId, action, target, audit, clock);
}

// Platform-level gate: the caller must be a platform admin (provisioning tenants, fleet ops). A denial is
// audited with no tenant (platform-level). Returns a 403 on failure, null when authorized.
static IResult? ForbidPlatform(HttpContext ctx, string action, string target, IAuditLog audit, IClock clock)
{
    var caller = (CallerPrincipal)ctx.Items["caller"]!;
    if (caller.IsPlatformAdmin)
    {
        return null;
    }

    audit.Record(new AuditRecord(caller.CallerId, action, target, clock.UtcNow, "denied"));
    return Results.Json(
        new ApiError("Forbidden", "This is a platform-admin operation."),
        statusCode: StatusCodes.Status403Forbidden);
}

// Records a successful, authorized mutation. Tenant-scoped so a durable store routes it to that tenant's
// database; pass tenantId null for a platform-level action.
static void Audit(HttpContext ctx, string action, string target, IAuditLog audit, IClock clock, string? tenantId = null)
{
    var caller = (CallerPrincipal)ctx.Items["caller"]!;
    audit.Record(new AuditRecord(caller.CallerId, action, target, clock.UtcNow, "allowed", tenantId));
}

// The tenant portion of a "tenant/room" room label (as RoomScopedMetrics keys them).
static string TenantOf(string roomLabel)
{
    var slash = roomLabel.IndexOf('/');
    return slash > 0 ? roomLabel[..slash] : roomLabel;
}

// Selects the game implementation for a room by its game id. (Step 3 will source this
// from the control-plane catalog; for now it mirrors the catalog's seeded games.)
static IGameSimulation GameFor(GameId gameId) => gameId.Value switch
{
    "grid-walk" => new GridWalkGame(),
    _ => new MoveRightGame(),
};

// Exposes the top-level-statement entry point as a referencible type so integration tests
// can boot the real host via WebApplicationFactory<Program>. No behavior; declaration only.
public partial class Program;

/// <summary>
/// One control-plane API key as bound from configuration (ControlPlane:ApiKeys). Each entry
/// maps a bearer key to the principal it grants. Used only outside Development, where keys
/// must be supplied by configuration rather than hardcoded.
/// </summary>
public sealed class ApiKeyEntry
{
    public string? Key { get; set; }
    public string? CallerId { get; set; }
    public string? TenantId { get; set; }
    public List<string>? Roles { get; set; }
}

/// <summary>
/// One seed tenant as bound from configuration (ControlPlane:Tenants). The tenant registry is seeded with
/// these at startup so the realtime edge can resolve them; further tenants are added at runtime via the
/// CRUD API (no code change). When omitted, a dev default (tenant-a/tenant-b) is seeded.
/// </summary>
public sealed class TenantSeedEntry
{
    public string? TenantId { get; set; }
    public string? DisplayName { get; set; }
}

/// <summary>
/// A matchmaking ticket submission. The scope (tenant/game/version) is authoritative: the caller may
/// only submit for a tenant it is authorized for, and a ticket only ever matches within its scope.
/// </summary>
public sealed record MatchmakingTicketRequest(string TenantId, string GameId, int GameVersion, string PlayerId, int Skill = 0);

/// <summary>
/// An <see cref="IReadinessCheck"/> backed by a probe delegate, so each dependency's readiness logic
/// is wired inline at the composition root (the only place that knows the concrete dependencies)
/// without a bespoke class per dependency. A probe that throws is surfaced as not-ready by the
/// <c>/ready</c> endpoint (fail-closed), never propagated.
/// </summary>
public sealed class DelegateReadinessCheck : IReadinessCheck
{
    private readonly Func<ReadinessResult> _probe;

    public DelegateReadinessCheck(string name, Func<ReadinessResult> probe)
    {
        Name = name;
        _probe = probe;
    }

    public string Name { get; }

    public ReadinessResult Check() => _probe();
}
