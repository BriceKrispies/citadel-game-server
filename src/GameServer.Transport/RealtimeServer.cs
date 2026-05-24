using System.Collections.Concurrent;
using GameServer.Identity;
using GameServer.Observability;
using GameServer.Persistence;
using GameServer.Protocol;
using GameServer.Replication;
using GameServer.Routing;
using GameServer.Simulation;
using GameServer.Tenancy;

namespace GameServer.Transport;

/// <summary>
/// The realtime data-plane composition for the first vertical slice. It owns the
/// per-connection protocol handling (hello → welcome → join → command) and the
/// tick driver that advances a room and fans authoritative snapshots out to that
/// room's connected clients.
/// </summary>
/// <remarks>
/// This sits at the transport edge: it depends inward on routing, tenancy,
/// simulation, persistence, and observability, and never the other way around.
/// It deliberately does not auto-tick — ticking is driven explicitly so runs stay
/// deterministic. A production host will drive <see cref="TickRoom"/> on a cadence.
/// </remarks>
public sealed class RealtimeServer
{
    private readonly ITenantResolver _tenants;
    private readonly ISessionRouter _router;
    private readonly ISnapshotStore<RoomKey, RoomSnapshot> _snapshots;
    private readonly IEventLog<RoomKey, RoomEvent> _events;
    private readonly ITelemetrySink _telemetry;
    private readonly Func<GameId, ReplicationPolicy> _policyProvider;

    private readonly ConcurrentDictionary<RoomKey, ConcurrentDictionary<ConnectionId, Connection>> _subscribers = new();
    private readonly ConcurrentDictionary<RoomKey, Replicator> _replicators = new();

    public RealtimeServer(
        ITenantResolver tenants,
        ISessionRouter router,
        ISnapshotStore<RoomKey, RoomSnapshot> snapshots,
        IEventLog<RoomKey, RoomEvent> events,
        ITelemetrySink telemetry,
        Func<GameId, ReplicationPolicy>? policyProvider = null)
    {
        _tenants = tenants;
        _router = router;
        _snapshots = snapshots;
        _events = events;
        _telemetry = telemetry;
        // Per-game replication policy comes from the control plane; default to the
        // conservative everyone/full policy (which still benefits from batching).
        _policyProvider = policyProvider ?? (_ => ReplicationPolicy.Default);
    }

    /// <summary>
    /// Drives one connection until its client side completes, processing each
    /// inbound message in order. Returns when the client closes the connection.
    /// </summary>
    /// <remarks>
    /// Slice limitation: a connection is added to the room's subscriber set on join
    /// but is not pruned when this loop ends. Graceful disconnect/unsubscribe is a
    /// planned milestone (see ARCHITECTURE.md) — do not assume cleanup happens here.
    /// </remarks>
    public async Task HandleConnectionAsync(IBidirectionalTransport transport, JoinTokenClaims principal, CancellationToken cancellationToken = default)
    {
        var connection = new Connection(transport, principal);
        _telemetry.Increment(TelemetryMetrics.ConnectionsOpened);
        _telemetry.Event(TelemetryEvents.ConnectionOpened, Tags(("connectionId", transport.ConnectionId.Value)));

        while (await transport.ReceiveAsync(cancellationToken).ConfigureAwait(false) is { } inbound)
        {
            _telemetry.Increment(TelemetryMetrics.MessagesIn, Tags(("messageType", inbound.MessageType.ToString())));
            await ProcessAsync(connection, inbound, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Room keys that currently have at least one subscriber, for the tick driver.</summary>
    public IReadOnlyCollection<RoomKey> ActiveRooms => _subscribers.Keys.ToArray();

    /// <summary>
    /// Advances the room one tick, persists the snapshot and events, then runs the
    /// replication pipeline (interest → delta → budget, per the game's policy) and
    /// sends exactly ONE batched <see cref="ServerSnapshot"/> per connection. This
    /// replaces the old O(N²) per-player fan-out with one policy-shaped message per
    /// viewer.
    /// </summary>
    public async Task TickRoom(RoomKey key, CancellationToken cancellationToken = default)
    {
        if (!_router.TryGetRoom(key, out var room))
        {
            return;
        }

        // The room is the only thing mutating its state; serialize tick against
        // concurrent command admission/join from other connection threads.
        TickResult result;
        IReadOnlyList<EntitySnapshot> world;
        lock (room)
        {
            result = room.Tick();
            // Project the post-tick state under the same lock for a consistent view.
            world = room.Project();
        }

        _snapshots.Save(key, result.Snapshot);
        foreach (var roomEvent in result.Events)
        {
            _events.Append(key, roomEvent);
        }

        if (!_subscribers.TryGetValue(key, out var connections))
        {
            return;
        }

        var tick = result.Snapshot.Tick;
        var replicator = _replicators.GetOrAdd(key, _ => new Replicator(ReplicationPolicy.Default));

        // The game produced `world` (opaque payloads + a per-entity version that changes
        // when the entity changes + a relevance key). The platform never interprets the
        // payload; it only sequences, deltas, budgets, and fans out.
        var byEntity = new Dictionary<EntityId, EntitySnapshot>(world.Count);
        foreach (var entity in world)
        {
            byEntity[entity.Id] = entity;
        }

        var byViewer = new Dictionary<string, Connection>();
        var viewers = new List<Viewer>(connections.Count);
        foreach (var connection in connections.Values)
        {
            if (connection.Player is not { } player)
            {
                continue;
            }

            byViewer[connection.Id.Value] = connection;
            // A viewer's relevance key is its own entity's key (e.g. its position), so
            // spatial interest works for any game; absent an entity it filters as None.
            var relevance = byEntity.TryGetValue(new EntityId(player.Value), out var self) ? self.Key : RelevanceKey.None;
            viewers.Add(new Viewer(new ViewerId(connection.Id.Value), relevance));
        }

        var traceId = $"snapshot-{key.RoomId.Value}-{tick}";
        var dropped = new List<ConnectionId>();

        foreach (var message in replicator.Replicate(viewers, world, tick))
        {
            if (!byViewer.TryGetValue(message.Viewer.Value, out var connection))
            {
                continue;
            }

            var entities = new List<EntityState>(message.Entities.Count);
            foreach (var entity in message.Entities)
            {
                entities.Add(new EntityState(entity.Id.Value, entity.Payload));
            }

            // Replicated payload volume (entities actually sent) — the metric that drops
            // under delta/interest/budget even when message count stays flat.
            _telemetry.Measure(TelemetryMetrics.SnapshotEntities, entities.Count);

            try
            {
                var envelope = connection.BuildSnapshot(tick, entities, traceId);
                await connection.Transport.SendAsync(envelope, cancellationToken).ConfigureAwait(false);
                _telemetry.Increment(TelemetryMetrics.MessagesOut, Tags(("messageType", nameof(MessageType.ServerSnapshot))));
                // The delta baseline advances only when the client acknowledges this
                // tick (see HandleAckAsync); a successful send is not proof of receipt.
            }
            catch (Exception ex)
            {
                // The connection is no longer writable (e.g. closed socket): drop it
                // so it stops receiving fan-out. Observable, never silent.
                dropped.Add(connection.Id);
                _telemetry.Event(TelemetryEvents.ConnectionDropped,
                    Tags(("connectionId", connection.Id.Value), ("reason", ex.GetType().Name)));
            }
        }

        foreach (var id in dropped)
        {
            connections.TryRemove(id, out _);
        }

        _telemetry.Increment(TelemetryMetrics.SnapshotsEmitted);
        _telemetry.Event(TelemetryEvents.SnapshotEmitted, Tags(("roomId", key.RoomId.Value), ("tick", result.Snapshot.Tick.ToString())));
    }

    private async Task ProcessAsync(Connection connection, MessageEnvelope inbound, CancellationToken cancellationToken)
    {
        switch (inbound.MessageType)
        {
            case MessageType.ClientHello:
                await HandleHelloAsync(connection, inbound, cancellationToken).ConfigureAwait(false);
                break;
            case MessageType.ClientJoinRoom:
                await HandleJoinAsync(connection, inbound, cancellationToken).ConfigureAwait(false);
                break;
            case MessageType.ClientCommand:
                await HandleCommandAsync(connection, inbound, cancellationToken).ConfigureAwait(false);
                break;
            case MessageType.ClientAck:
                await HandleAckAsync(connection, inbound, cancellationToken).ConfigureAwait(false);
                break;
            default:
                await RejectAsync(connection, inbound, ServerErrorCode.InvalidCommand,
                    $"Unsupported client message type '{inbound.MessageType}'.", cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleHelloAsync(Connection connection, MessageEnvelope inbound, CancellationToken cancellationToken)
    {
        // The verified join token is authoritative for identity: a client may not
        // declare a tenant or game other than the one its token authorizes.
        if (inbound.TenantId.Value != connection.Principal.TenantId || inbound.GameId.Value != connection.Principal.GameId)
        {
            _telemetry.Event(TelemetryEvents.IdentityRejected, Tags(
                ("connectionId", connection.Id.Value),
                ("declaredTenant", inbound.TenantId.Value),
                ("authorizedTenant", connection.Principal.TenantId)));
            await RejectAsync(connection, inbound, ServerErrorCode.Unauthorized,
                "ClientHello identity does not match the authorized join token.", cancellationToken).ConfigureAwait(false);
            return;
        }

        connection.TenantId = inbound.TenantId;
        connection.Game = inbound.GameId;

        if (!ProtocolVersions.IsSupported(inbound.ProtocolVersion))
        {
            _telemetry.Event(TelemetryEvents.ProtocolRejected, Tags(("requested", inbound.ProtocolVersion.ToString())));
            await RejectAsync(connection, inbound, ServerErrorCode.UnsupportedProtocolVersion,
                $"Protocol version {inbound.ProtocolVersion} is not supported.", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!_tenants.TryResolve(inbound.TenantId, out var tenant))
        {
            _telemetry.Event(TelemetryEvents.TenantRejected, Tags(("tenantId", inbound.TenantId.Value)));
            await RejectAsync(connection, inbound, ServerErrorCode.UnknownTenant,
                $"Unknown tenant '{inbound.TenantId}'.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var session = _router.CreateSession(tenant);
        connection.Session = session;
        _telemetry.Event(TelemetryEvents.SessionCreated, Tags(("sessionId", session.Id.Value), ("tenantId", tenant.TenantId.Value)));

        var welcome = connection.BuildServerMessage(
            MessageType.ServerWelcome,
            new ServerWelcome(session.Id, ProtocolVersions.Current),
            inbound.TraceId);
        await SendAsync(connection, welcome, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleJoinAsync(Connection connection, MessageEnvelope inbound, CancellationToken cancellationToken)
    {
        if (connection.Session is not { } session)
        {
            await RejectAsync(connection, inbound, ServerErrorCode.InvalidCommand,
                "ClientHello must precede ClientJoinRoom.", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (inbound.Payload is not ClientJoinRoom join)
        {
            await RejectAsync(connection, inbound, ServerErrorCode.InvalidCommand, "Malformed join payload.", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (inbound.PlayerId is not { } player)
        {
            await RejectAsync(connection, inbound, ServerErrorCode.InvalidCommand, "Join requires a playerId.", cancellationToken).ConfigureAwait(false);
            return;
        }

        // The token authorizes one room and one player; reject any attempt to join as
        // a different player or into a room the token was not minted for.
        if (join.RoomId.Value != connection.Principal.RoomId || player.Value != connection.Principal.PlayerId)
        {
            _telemetry.Event(TelemetryEvents.IdentityRejected, Tags(
                ("connectionId", connection.Id.Value),
                ("declaredRoom", join.RoomId.Value),
                ("declaredPlayer", player.Value),
                ("authorizedRoom", connection.Principal.RoomId),
                ("authorizedPlayer", connection.Principal.PlayerId)));
            await RejectAsync(connection, inbound, ServerErrorCode.Unauthorized,
                "Join does not match the authorized join token's room/player.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var room = _router.GetOrCreateRoom(session.Tenant, join.RoomId, connection.Game ?? new GameId("unknown"));
        lock (room)
        {
            room.Join(player);
        }

        var key = new RoomKey(session.Tenant.TenantId, join.RoomId);
        connection.JoinRoom(key, player);
        _subscribers.GetOrAdd(key, _ => new ConcurrentDictionary<ConnectionId, Connection>())[connection.Id] = connection;
        var replicator = _replicators.GetOrAdd(key, _ => new Replicator(_policyProvider(connection.Game ?? new GameId("unknown"))));
        // A (re)joining connection cannot be assumed to hold any prior baseline: reset
        // it so the next tick re-establishes a full keyframe for this viewer.
        replicator.Resubscribe(new ViewerId(connection.Id.Value));

        _telemetry.Event(TelemetryEvents.RoomJoined, Tags(("roomId", join.RoomId.Value), ("playerId", player.Value)));
    }

    private async Task HandleCommandAsync(Connection connection, MessageEnvelope inbound, CancellationToken cancellationToken)
    {
        if (connection.Session is not { } session || connection.RoomKey is not { } key || connection.Player is not { } player)
        {
            await RejectAsync(connection, inbound, ServerErrorCode.NotJoined, "Join a room before sending commands.", cancellationToken).ConfigureAwait(false);
            return;
        }

        // Never trust the client's claimed tenant after the handshake: the session's
        // resolved tenant is authoritative.
        if (!inbound.TenantId.Equals(session.Tenant.TenantId))
        {
            await RejectAsync(connection, inbound, ServerErrorCode.InvalidCommand, "Tenant mismatch for session.", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (inbound.Payload is not ClientCommand command)
        {
            await RejectAsync(connection, inbound, ServerErrorCode.InvalidCommand, "Malformed command payload.", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!_router.TryGetRoom(key, out var room))
        {
            await RejectAsync(connection, inbound, ServerErrorCode.RoomUnavailable, "Room is no longer available.", cancellationToken).ConfigureAwait(false);
            return;
        }

        // The command is a game-defined string; the room's game decides legality.
        CommandAdmission admission;
        lock (room)
        {
            admission = room.TryEnqueue(player, command.Command, inbound.Sequence);
        }

        switch (admission)
        {
            case CommandAdmission.Accepted:
                _telemetry.Increment(TelemetryMetrics.CommandsAccepted);
                break;
            case CommandAdmission.RejectedStaleSequence:
                await RejectAsync(connection, inbound, ServerErrorCode.StaleSequence,
                    $"Sequence {inbound.Sequence} is stale.", cancellationToken).ConfigureAwait(false);
                break;
            case CommandAdmission.RejectedNotJoined:
                await RejectAsync(connection, inbound, ServerErrorCode.NotJoined, "Player is not in the room.", cancellationToken).ConfigureAwait(false);
                break;
            case CommandAdmission.RejectedInvalidCommand:
                await RejectAsync(connection, inbound, ServerErrorCode.InvalidCommand,
                    $"Command '{command.Command}' is not valid for this game.", cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(admission), admission, "Unhandled admission outcome.");
        }
    }

    private Task HandleAckAsync(Connection connection, MessageEnvelope inbound, CancellationToken cancellationToken)
    {
        // An ack only matters once the connection has joined a room (it then has a
        // viewer identity and a replicator). It advances the viewer's delta baseline
        // to the tick the client confirmed receiving — the authoritative signal that
        // the client holds that state, which a successful send does not prove.
        if (connection.RoomKey is { } key
            && inbound.Payload is ClientAck ack
            && _replicators.TryGetValue(key, out var replicator))
        {
            replicator.Acknowledge(new ViewerId(connection.Id.Value), ack.AckedServerTick);
            _telemetry.Increment(TelemetryMetrics.ClientAcks);
        }

        return Task.CompletedTask;
    }

    private async Task RejectAsync(Connection connection, MessageEnvelope inbound, ServerErrorCode code, string message, CancellationToken cancellationToken)
    {
        _telemetry.Increment(TelemetryMetrics.CommandsRejected, Tags(("code", code.ToString())));
        _telemetry.Increment(TelemetryMetrics.InvalidMessages, Tags(("messageType", inbound.MessageType.ToString())));
        _telemetry.Event(TelemetryEvents.CommandRejected, Tags(("code", code.ToString()), ("traceId", inbound.TraceId)));

        var error = connection.BuildServerMessage(MessageType.ServerError, new ServerError(code, message), inbound.TraceId);
        await SendAsync(connection, error, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendAsync(Connection connection, MessageEnvelope envelope, CancellationToken cancellationToken)
    {
        await connection.Transport.SendAsync(envelope, cancellationToken).ConfigureAwait(false);
        _telemetry.Increment(TelemetryMetrics.MessagesOut, Tags(("messageType", envelope.MessageType.ToString())));
    }

    private static IReadOnlyDictionary<string, string> Tags(params (string Key, string Value)[] pairs)
    {
        var tags = new Dictionary<string, string>(pairs.Length);
        foreach (var (key, value) in pairs)
        {
            tags[key] = value;
        }

        return tags;
    }

    /// <summary>Per-connection state: identity, the resolved session, room placement, and the outbound sequence.</summary>
    private sealed class Connection
    {
        private long _outboundSequence;

        public Connection(IServerPushTransport transport, JoinTokenClaims principal)
        {
            Transport = transport;
            Principal = principal;
        }

        public IServerPushTransport Transport { get; }

        /// <summary>The verified identity this connection is authorized for (from its join token).</summary>
        public JoinTokenClaims Principal { get; }

        public ConnectionId Id => Transport.ConnectionId;

        public TenantId? TenantId { get; set; }
        public GameId? Game { get; set; }
        public Session? Session { get; set; }
        public RoomKey? RoomKey { get; private set; }
        public PlayerId? Player { get; private set; }

        public void JoinRoom(RoomKey key, PlayerId player)
        {
            RoomKey = key;
            Player = player;
        }

        public MessageEnvelope BuildServerMessage(MessageType type, IMessagePayload payload, string traceId) =>
            new()
            {
                TenantId = TenantId ?? new TenantId("unknown"),
                GameId = Game ?? new GameId("unknown"),
                RoomId = RoomKey?.RoomId,
                SessionId = Session?.Id,
                PlayerId = Player,
                ProtocolVersion = ProtocolVersions.Current,
                MessageType = type,
                Sequence = Interlocked.Increment(ref _outboundSequence),
                TraceId = traceId,
                Payload = payload,
            };

        /// <summary>
        /// Builds one batched <see cref="ServerSnapshot"/> envelope carrying every
        /// entity relevant to this connection. The envelope's PlayerId is the recipient;
        /// each <see cref="EntityState"/> carries the game's opaque bytes.
        /// </summary>
        public MessageEnvelope BuildSnapshot(long tick, IReadOnlyList<EntityState> entities, string traceId) =>
            new()
            {
                TenantId = TenantId ?? new TenantId("unknown"),
                GameId = Game ?? new GameId("unknown"),
                RoomId = RoomKey?.RoomId,
                SessionId = Session?.Id,
                PlayerId = Player,
                ProtocolVersion = ProtocolVersions.Current,
                MessageType = MessageType.ServerSnapshot,
                Sequence = Interlocked.Increment(ref _outboundSequence),
                TraceId = traceId,
                Payload = new ServerSnapshot(tick, entities),
            };
    }
}
