using GameServer.Identity;
using GameServer.Observability;
using GameServer.Persistence;
using GameServer.Protocol;
using GameServer.Replication;
using GameServer.Routing;
using GameServer.Simulation;
using GameServer.Tenancy;
using GameServer.Transport;

namespace GameServer.IntegrationTests;

/// <summary>
/// Wires the real in-process realtime stack from production types only (no test-only
/// helpers): tenant resolver, session router with an injectable game factory, in-memory
/// stores, the aggregating telemetry sink the host uses, and a <see cref="RealtimeServer"/>.
/// Scenarios drive it at scale and assert on observable behavior + telemetry, then write
/// artifacts. Reused across the gap scenarios (#1-#6).
/// </summary>
public sealed class IntegrationHarness
{
    public IntegrationHarness(
        Func<GameId, IGameSimulation> gameFactory,
        IEnumerable<string>? tenants = null,
        Func<GameId, ReplicationPolicy>? policy = null,
        int maxQueueDepth = GameRoom.DefaultMaxQueueDepth,
        RoomLifecycle lifecycle = RoomLifecycle.Persist,
        AdmissionPolicy? admission = null,
        int eventLogRetentionTicks = 0,
        ITenantRateLimiter? rateLimiter = null,
        ITenantMetricsSink? tenantMetrics = null,
        int maxCommandBytes = 0,
        IDegradationController? degradation = null)
    {
        var contexts = (tenants ?? new[] { "tenant-a" })
            .Select(t => new TenantContext(new TenantId(t), $"Tenant {t}"));
        Tenants = new InMemoryTenantResolver(contexts);

        Router = new InMemorySessionRouter((roomId, gameId) =>
            new GameRoom(roomId, gameFactory(gameId), new LogicalSimulationClock(), new SeededRandomSource(), maxQueueDepth));

        Snapshots = new InMemorySnapshotStore<RoomKey, RoomSnapshot>();
        Events = new InMemoryEventLog<RoomKey, RoomEvent>(e => e.Tick);
        Telemetry = new AggregatingTelemetrySink();
        Server = new RealtimeServer(
            Tenants, Router, Snapshots, Events, Telemetry, policy, lifecycle, admission, eventLogRetentionTicks,
            rateLimiter: rateLimiter, tenantMetrics: tenantMetrics, maxCommandBytes: maxCommandBytes,
            degradation: degradation);
    }

    public InMemoryTenantResolver Tenants { get; }
    public InMemorySessionRouter Router { get; }
    public InMemorySnapshotStore<RoomKey, RoomSnapshot> Snapshots { get; }
    public InMemoryEventLog<RoomKey, RoomEvent> Events { get; }
    public AggregatingTelemetrySink Telemetry { get; }
    public RealtimeServer Server { get; }

    public RoomKey Key(string tenant, string room) => new(new TenantId(tenant), new RoomId(room));

    /// <summary>The per-room tick delegate the schedulers drive.</summary>
    public Func<RoomKey, CancellationToken, Task> TickFn => (room, ct) => Server.TickRoom(room, ct);

    /// <summary>
    /// Connects a client, performs hello + join, then closes it. The connection remains a
    /// subscriber afterwards, so the room stays active (tickable) for the scenario. Returns
    /// the transport so a scenario can read what the server pushed to that client.
    /// </summary>
    public Task<InMemoryBidirectionalTransport> JoinAsync(
        string tenant, string room, string player, string game = "demo") =>
        RunClientAsync(tenant, room, player, game, commands: Array.Empty<string>());

    /// <summary>
    /// Connects a client, performs hello + join, enqueues the given commands (monotonic
    /// per-player sequence), then closes it. Commands are queued for the next tick; the
    /// scenario calls <see cref="RealtimeServer.TickRoom"/> to apply them. The connection
    /// remains a subscriber afterwards. Returns the transport for reading pushed messages.
    /// </summary>
    public async Task<InMemoryBidirectionalTransport> RunClientAsync(
        string tenant, string room, string player, string game, IReadOnlyList<string> commands)
    {
        var transport = new InMemoryBidirectionalTransport(new ConnectionId($"{tenant}:{room}:{player}:{Guid.NewGuid():n}"));
        transport.ClientSend(Envelope(MessageType.ClientHello, new ClientHello(player), tenant, game, room: null, player, sequence: 0));
        transport.ClientSend(Envelope(MessageType.ClientJoinRoom, new ClientJoinRoom(new RoomId(room)), tenant, game, room, player, sequence: 0));

        long sequence = 1;
        foreach (var command in commands)
        {
            transport.ClientSend(Envelope(MessageType.ClientCommand, new ClientCommand(command), tenant, game, room, player, sequence++));
        }

        transport.CompleteClient();

        var claims = new JoinTokenClaims(
            tenant, game, room, player, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1));
        await Server.HandleConnectionAsync(transport, claims);
        return transport;
    }

    /// <summary>
    /// Connects, performs hello + join, then sends an explicit <c>ClientLeaveRoom</c> before
    /// closing — exercising the leave-without-disconnect path (membership freed, <c>OnLeave</c>
    /// fired, a <c>ServerEvent</c> emitted). Returns the transport so a scenario can read pushes.
    /// </summary>
    public async Task<InMemoryBidirectionalTransport> RunClientWithLeaveAsync(
        string tenant, string room, string player, string game = "demo")
    {
        var transport = new InMemoryBidirectionalTransport(new ConnectionId($"{tenant}:{room}:{player}:{Guid.NewGuid():n}"));
        transport.ClientSend(Envelope(MessageType.ClientHello, new ClientHello(player), tenant, game, room: null, player, sequence: 0));
        transport.ClientSend(Envelope(MessageType.ClientJoinRoom, new ClientJoinRoom(new RoomId(room)), tenant, game, room, player, sequence: 0));
        transport.ClientSend(Envelope(MessageType.ClientLeaveRoom, new ClientLeaveRoom(new RoomId(room)), tenant, game, room, player, sequence: 0));
        transport.CompleteClient();

        var claims = new JoinTokenClaims(
            tenant, game, room, player, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1));
        await Server.HandleConnectionAsync(transport, claims);
        return transport;
    }

    /// <summary>
    /// Opens a connection and leaves it open (the loop parks awaiting more input). Because
    /// admission runs synchronously before the first await, the returned connection has
    /// already been admitted-or-rejected by the time this returns. Call
    /// <see cref="OpenConnection.CloseAsync"/> to end it. Used to exercise concurrent caps.
    /// </summary>
    public OpenConnection OpenConnectionFor(string tenant, string player, string game = "demo", string room = "arena")
    {
        var transport = new InMemoryBidirectionalTransport(new ConnectionId($"{tenant}:{player}:{Guid.NewGuid():n}"));
        var claims = new JoinTokenClaims(
            tenant, game, room, player, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1));
        var loop = Server.HandleConnectionAsync(transport, claims);
        return new OpenConnection(transport, loop);
    }

    /// <summary>A held-open connection: its transport (to read pushes) and the server loop task.</summary>
    public sealed record OpenConnection(InMemoryBidirectionalTransport Transport, Task Loop)
    {
        public bool WasRejected => Transport.DrainOutbound().Any(m => m.Payload is ServerError);

        public async Task CloseAsync()
        {
            Transport.CompleteClient();
            await Loop;
        }
    }

    private static MessageEnvelope Envelope(
        MessageType type, IMessagePayload payload, string tenant, string game, string? room, string player, long sequence) =>
        new()
        {
            TenantId = new TenantId(tenant),
            GameId = new GameId(game),
            RoomId = room is null ? null : new RoomId(room),
            SessionId = null,
            PlayerId = new PlayerId(player),
            ProtocolVersion = ProtocolVersions.Current,
            MessageType = type,
            Sequence = sequence,
            TraceId = $"{type}-{player}-{sequence}",
            Payload = payload,
        };
}
