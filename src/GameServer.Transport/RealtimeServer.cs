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
/// What happens to a room when its last connection leaves.
/// </summary>
public enum RoomLifecycle
{
    /// <summary>
    /// Keep the room (and its state) after everyone disconnects, ready for a fast rejoin.
    /// Without an external reaper this leaks: every distinct room ever touched lingers
    /// forever. This was the original, implicit behavior.
    /// </summary>
    Persist,

    /// <summary>
    /// Tear the room down when the last connection leaves — drop the subscriber set, the
    /// replicator, and the routed room. A later join recreates it. Bounds memory by live
    /// rooms rather than by all rooms ever seen.
    /// </summary>
    Reap,
}

/// <summary>
/// Global admission limits enforced at the edge so a burst cannot drive the server past
/// its capacity. Defaults are unbounded (no admission control), so this is opt-in; a host
/// sets real ceilings. Rejections are explicit (the client gets a <c>ServerError</c>) and
/// counted (<c>admission_rejected</c> with a reason), never silent.
/// </summary>
/// <remarks>
/// Both global and per-tenant ceilings are enforced at the edge: <see cref="MaxConnections"/> /
/// <see cref="MaxConnectionsPerTenant"/> on connect, and <see cref="MaxRooms"/> /
/// <see cref="MaxRoomsPerTenant"/> on room creation (see <c>HandleJoinAsync</c>), so one tenant
/// cannot exhaust global room capacity and starve the others. Pinned by
/// <c>PerTenantRoomCapScenario</c>.
/// </remarks>
public sealed record AdmissionPolicy(
    int MaxConnections = int.MaxValue,
    int MaxConnectionsPerTenant = int.MaxValue,
    int MaxRooms = int.MaxValue,
    int MaxRoomsPerTenant = int.MaxValue)
{
    public static readonly AdmissionPolicy Unlimited = new();
}

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
    private readonly RoomLifecycle _lifecycle;
    private readonly AdmissionPolicy _admission;
    // 0 = unbounded (never truncate). >0 keeps that many trailing ticks of events; older
    // events are covered by the saved snapshot and compacted away.
    private readonly int _eventLogRetentionTicks;
    // How long a connection may stay silent before the handshake (pre-ClientHello) or while
    // established (post-hello) before it is reaped, so half-open/zombie sockets cannot accumulate.
    private readonly TimeSpan _handshakeTimeout;
    private readonly IIdleConnectionPolicy _idlePolicy;
    // Cluster ownership: when wired, this node releases a room's directory claim as the room is reaped
    // so ownership tracks live rooms (otherwise OwnedCount only grows). Null when clustering is not
    // configured (e.g. unit harnesses) — then ownership is not touched here.
    private readonly IRoomDirectory? _roomDirectory;
    private readonly NodeId _localNode;

    // Per-tenant ingress rate limiting — the core noisy-neighbor control on the message path. Null
    // when not configured (unit harnesses): then no rate limiting is applied. When wired (the
    // deployed host), every inbound command is metered against the sender's tenant bucket BEFORE it
    // is enqueued, so one tenant's flood is shed to its fair share without touching another tenant's
    // allowance (see HandleCommandAsync). Wired only at the composition root.
    private readonly ITenantRateLimiter? _rateLimiter;
    // Per-tenant inbound attribution. Null when not configured. When wired, every inbound message is
    // counted against the sender's tenant here so an operator can rank tenants by message rate during
    // a noisy-neighbor incident — something the tag-folding global sink cannot answer.
    private readonly ITenantMetricsSink? _tenantMetrics;
    // Kernel-level inbound command-size bound (bytes of the decoded command string). 0 = unbounded.
    // This complements the transport-level frame cap (Realtime:MaxFrameBytes drops oversize WS
    // frames at the socket): it guards paths the frame cap does not cover (the in-memory transport,
    // and any decoded payload that slipped under the frame budget). An oversize command is shed with
    // a typed ServerError and the connection SURVIVES — one bad frame is not a disconnect.
    private readonly int _maxCommandBytes;

    // Admission counters. Guarded by _admissionLock (per-connection, not on the hot path).
    private readonly object _admissionLock = new();
    private readonly Dictionary<string, int> _connectionsPerTenant = new();
    private int _connectionCount;

    private readonly ConcurrentDictionary<RoomKey, ConcurrentDictionary<ConnectionId, Connection>> _subscribers = new();
    private readonly ConcurrentDictionary<RoomKey, Replicator> _replicators = new();
    // Serializes a room's join (subscribe + create) against its teardown so the two can
    // never interleave. Only taken on the Reap path; Persist keeps the original behavior.
    private readonly ConcurrentDictionary<RoomKey, object> _roomLocks = new();

    public RealtimeServer(
        ITenantResolver tenants,
        ISessionRouter router,
        ISnapshotStore<RoomKey, RoomSnapshot> snapshots,
        IEventLog<RoomKey, RoomEvent> events,
        ITelemetrySink telemetry,
        Func<GameId, ReplicationPolicy>? policyProvider = null,
        RoomLifecycle lifecycle = RoomLifecycle.Persist,
        AdmissionPolicy? admission = null,
        int eventLogRetentionTicks = 0,
        TimeSpan? handshakeTimeout = null,
        IIdleConnectionPolicy? idlePolicy = null,
        IRoomDirectory? roomDirectory = null,
        NodeId localNode = default,
        ITenantRateLimiter? rateLimiter = null,
        ITenantMetricsSink? tenantMetrics = null,
        int maxCommandBytes = 0)
    {
        _tenants = tenants;
        _roomDirectory = roomDirectory;
        _localNode = localNode;
        _rateLimiter = rateLimiter;
        _tenantMetrics = tenantMetrics;
        _maxCommandBytes = maxCommandBytes;
        _router = router;
        _snapshots = snapshots;
        _events = events;
        _telemetry = telemetry;
        // Per-game replication policy comes from the control plane; default to the
        // conservative everyone/full policy (which still benefits from batching).
        _policyProvider = policyProvider ?? (_ => ReplicationPolicy.Default);
        _lifecycle = lifecycle;
        _admission = admission ?? AdmissionPolicy.Unlimited;
        _eventLogRetentionTicks = eventLogRetentionTicks;
        // A connection that never completes the handshake is reaped quickly (slowloris/zombie
        // protection); an established connection gets the generous idle window from the policy.
        _handshakeTimeout = handshakeTimeout ?? TimeSpan.FromSeconds(1);
        _idlePolicy = idlePolicy ?? new HeartbeatIdlePolicy(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30));
    }

    private object RoomLock(RoomKey key) => _roomLocks.GetOrAdd(key, _ => new object());

    /// <summary>Rooms currently placed for a tenant — the basis for the per-tenant room ceiling.</summary>
    private int TenantRoomCount(TenantId tenant)
    {
        var count = 0;
        foreach (var key in _subscribers.Keys)
        {
            if (key.TenantId.Equals(tenant))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Connections currently admitted and open. A capacity gauge for scenarios/ops.</summary>
    public int ActiveConnectionCount
    {
        get { lock (_admissionLock) { return _connectionCount; } }
    }

    /// <summary>
    /// The admission policy this server is enforcing. Exposed so operators (and tests) can
    /// confirm the deployed host actually configured ceilings rather than running unbounded.
    /// </summary>
    public AdmissionPolicy Admission => _admission;

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

        // Admission control runs before anything else: a connection that cannot be
        // admitted is told so and dropped, before it can consume a session or a room.
        if (!TryAdmitConnection(principal.TenantId, out var reason))
        {
            _telemetry.Increment(TelemetryMetrics.AdmissionRejected, Tags(("reason", reason)));
            await SendConnectionError(transport, principal, ServerErrorCode.Overloaded,
                $"Server is at capacity ({reason}); retry later.", cancellationToken).ConfigureAwait(false);
            return;
        }

        _telemetry.Increment(TelemetryMetrics.ConnectionsOpened);
        _telemetry.Event(TelemetryEvents.ConnectionOpened, Tags(("connectionId", transport.ConnectionId.Value)));

        // Some transports answer pings and drop frames at the edge, so those never surface here as
        // inbound messages. They are still proof the client is alive; without this probe a client
        // sending only such frames would look idle and be wrongly reaped (see IInboundActivityProbe).
        var activityProbe = transport as IInboundActivityProbe;

        try
        {
            // The pending receive is held across idle-deadline checks rather than re-issued, so a
            // frame that arrives while we were timing out is never dropped — the next wait observes it.
            var receiveTask = transport.ReceiveAsync(cancellationToken);
            while (true)
            {
                // Before the handshake completes a connection gets a short deadline; once
                // established it gets the policy's generous idle window. Either way a silent
                // connection is reaped rather than parking a task forever.
                var deadline = connection.Session is null ? _handshakeTimeout : _idlePolicy.IdleTimeout;
                var activityBefore = activityProbe?.InboundFrameCount ?? 0;

                MessageEnvelope? inbound;
                try
                {
                    inbound = await receiveTask.WaitAsync(deadline, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // Once established, wire frames the transport handled at the edge (a ping it
                    // answered, a frame it dropped) prove the client is alive — re-arm the deadline
                    // on the same pending receive. Before the handshake completes, the deadline is
                    // absolute slowloris protection: the client must actually finish the handshake,
                    // so ping noise does not buy it more time.
                    if (connection.Session is not null
                        && activityProbe is not null
                        && activityProbe.InboundFrameCount != activityBefore)
                    {
                        continue;
                    }

                    // Silent past the deadline (never said hello, or went quiet while established):
                    // a half-open/zombie connection. Reap it. Observable, never silent.
                    _telemetry.Event(TelemetryEvents.ConnectionDropped, Tags(
                        ("connectionId", transport.ConnectionId.Value),
                        ("reason", connection.Session is null ? "handshake_timeout" : "idle_timeout")));
                    break;
                }

                if (inbound is null)
                {
                    break; // the client closed the connection
                }

                _telemetry.Increment(TelemetryMetrics.MessagesIn, Tags(("messageType", inbound.MessageType.ToString())));
                // Attribute inbound volume to the connection's authorized tenant so an operator can
                // rank tenants during a noisy-neighbor incident (the global sink folds tags away and
                // cannot). The verified principal is authoritative — never the client's declared
                // envelope tenant — so a spoofed tenant tag cannot mis-attribute another's load.
                _tenantMetrics?.Record(principal.TenantId, TelemetryMetrics.MessagesIn, 1);
                await ProcessAsync(connection, inbound, cancellationToken).ConfigureAwait(false);
                receiveTask = transport.ReceiveAsync(cancellationToken);
            }
        }
        finally
        {
            // The client side completed (clean close) or the loop faulted: in either case
            // this connection is gone and must stop counting against the room and the
            // admission ceilings.
            OnDisconnect(connection);
            ReleaseConnection(principal.TenantId);
        }
    }

    private bool TryAdmitConnection(string tenant, out string reason)
    {
        lock (_admissionLock)
        {
            if (_connectionCount >= _admission.MaxConnections)
            {
                reason = "max_connections";
                return false;
            }

            _connectionsPerTenant.TryGetValue(tenant, out var perTenant);
            if (perTenant >= _admission.MaxConnectionsPerTenant)
            {
                reason = "tenant_quota";
                return false;
            }

            _connectionCount++;
            _connectionsPerTenant[tenant] = perTenant + 1;
            reason = string.Empty;
            return true;
        }
    }

    private void ReleaseConnection(string tenant)
    {
        lock (_admissionLock)
        {
            if (_connectionCount > 0)
            {
                _connectionCount--;
            }

            if (_connectionsPerTenant.TryGetValue(tenant, out var perTenant))
            {
                if (perTenant <= 1)
                {
                    _connectionsPerTenant.Remove(tenant);
                }
                else
                {
                    _connectionsPerTenant[tenant] = perTenant - 1;
                }
            }
        }
    }

    private async Task SendConnectionError(
        IServerPushTransport transport, JoinTokenClaims principal, ServerErrorCode code, string message, CancellationToken cancellationToken)
    {
        var envelope = new MessageEnvelope
        {
            TenantId = new TenantId(principal.TenantId),
            GameId = new GameId(principal.GameId),
            RoomId = null,
            SessionId = null,
            PlayerId = null,
            ProtocolVersion = ProtocolVersions.Current,
            MessageType = MessageType.ServerError,
            Sequence = 0,
            TraceId = "admission",
            Payload = new ServerError(code, message),
        };

        await transport.SendAsync(envelope, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a closed connection from its room. Under <see cref="RoomLifecycle.Reap"/>
    /// the room is also torn down once its last subscriber leaves, so memory tracks live
    /// rooms rather than every room ever touched. Under <see cref="RoomLifecycle.Persist"/>
    /// the original behavior is kept (the room and any subscriber bookkeeping linger).
    /// </summary>
    private void OnDisconnect(Connection connection)
    {
        _telemetry.Increment(TelemetryMetrics.ConnectionsClosed);

        if (connection.RoomKey is not { } key)
        {
            return;
        }

        // A disconnect while joined is a leave: signal the game so it can release the player's
        // state, regardless of the room lifecycle policy. The game owns the mutation; the room
        // lock serializes it against tick/join. Idempotent for a player already removed.
        if (connection.Player is { } leaving && _router.TryGetRoom(key, out var liveRoom))
        {
            lock (liveRoom)
            {
                liveRoom.Leave(leaving);
            }

            _telemetry.Event(TelemetryEvents.RoomLeft, Tags(("roomId", key.RoomId.Value), ("playerId", leaving.Value)));
        }

        if (_lifecycle != RoomLifecycle.Reap)
        {
            return;
        }

        lock (RoomLock(key))
        {
            if (!_subscribers.TryGetValue(key, out var connections))
            {
                return;
            }

            connections.TryRemove(connection.Id, out _);

            // The connection is gone for good under Reap: release its outbound buffer (and any
            // background writer) so it does not linger.
            _ = connection.Outbound.DisposeAsync();

            // Drop this viewer's delta baseline so it does not linger in the replicator.
            if (_replicators.TryGetValue(key, out var replicator))
            {
                replicator.Resubscribe(new ViewerId(connection.Id.Value));
            }

            if (connections.IsEmpty)
            {
                _subscribers.TryRemove(key, out _);
                _replicators.TryRemove(key, out _);
                _roomLocks.TryRemove(key, out _);
                // The room is being torn down (last subscriber left): signal the game once so it
                // can finalize, before the routed room is dropped.
                if (_router.TryGetRoom(key, out var reapedRoom))
                {
                    reapedRoom.Terminate();
                }

                _router.TryRemoveRoom(key);
                // Relinquish cluster ownership as the room goes away, so OwnedCount tracks live rooms
                // and another node can take this room later. No-op when clustering is not wired.
                _roomDirectory?.Release(key, _localNode);
                _telemetry.Event(TelemetryEvents.RoomClosed, Tags(("roomId", key.RoomId.Value), ("tenantId", key.TenantId.Value)));
            }
        }
    }

    /// <summary>Room keys that currently have at least one subscriber, for the tick driver.</summary>
    public IReadOnlyCollection<RoomKey> ActiveRooms => _subscribers.Keys.ToArray();

    /// <summary>
    /// Produces a read-only observation of a live room (authoritative tick, projected
    /// entities, and per-viewer lag) for operator/admin debugging — the "drop in and see
    /// what's happening" view. Returns false if the room is not currently placed. Never
    /// mutates room state: it projects under the room lock for a consistent view and reads
    /// only existing replicator/subscriber bookkeeping.
    /// </summary>
    public bool TryObserveRoom(RoomKey key, out RoomObservation observation)
    {
        observation = null!;
        if (!_router.TryGetRoom(key, out var room))
        {
            return false;
        }

        long tick;
        IReadOnlyList<EntitySnapshot> world;
        lock (room)
        {
            tick = room.Snapshot().Tick;
            world = room.Project();
        }

        var entities = world
            .Select(e => new ObservedEntity(e.Id.Value, e.Version, e.Key.X, e.Key.Y, e.Key.Group, Convert.ToBase64String(e.Payload)))
            .ToList();

        var viewers = new List<ObservedViewer>();
        _replicators.TryGetValue(key, out var replicator);
        if (_subscribers.TryGetValue(key, out var connections))
        {
            foreach (var connection in connections.Values)
            {
                var pending = replicator?.PendingSnapshots(new ViewerId(connection.Id.Value)) ?? 0;
                viewers.Add(new ObservedViewer(connection.Id.Value, connection.Player?.Value, pending));
            }
        }

        observation = new RoomObservation(key.TenantId.Value, key.RoomId.Value, tick, entities, viewers);
        return true;
    }

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
            // The backlog about to be drained — the backpressure gauge, captured before
            // the tick empties the queue.
            _telemetry.Measure(TelemetryMetrics.CommandQueueDepth, room.QueueDepth);
            result = room.Tick();
            // Project the post-tick state under the same lock for a consistent view.
            world = room.Project();
        }

        _snapshots.Save(key, result.Snapshot);
        foreach (var roomEvent in result.Events)
        {
            _events.Append(key, roomEvent);
        }

        if (_eventLogRetentionTicks > 0)
        {
            // The snapshot just saved captures all state through this tick, so events at or
            // below (tick - retention) are no longer needed for recovery: compact them away
            // and keep only a trailing window. This bounds an otherwise unbounded log.
            _events.TruncateThrough(key, result.Snapshot.Tick - _eventLogRetentionTicks);
        }

        if (!_subscribers.TryGetValue(key, out var connections))
        {
            return;
        }

        var tick = result.Snapshot.Tick;

        // The replicator is created once, at join, with the room's per-game policy (see
        // HandleJoinAsync). TickRoom is a strict reader: it must never create one here,
        // or it could shadow the real policy with the conservative default. Subscribers
        // exist (checked above) and a replicator is always established alongside them, so
        // a miss is an unreachable invariant break — skip fan-out rather than fabricate.
        if (!_replicators.TryGetValue(key, out var replicator))
        {
            return;
        }

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

            // Snapshot vs delta selection: a baseline-less (just-joined/reset) viewer gets a full
            // ServerSnapshot keyframe it can replace its view with; once it holds a baseline, it
            // gets a ServerDelta carrying only the changed/removed entities since its last ack.
            // The replicator decides keyframe-ness per viewer (see ReplicationMessage.IsKeyframe).
            // Hand the message to the connection's outbound buffer; this never awaits the socket,
            // so one slow client cannot stall the tick for everyone. The delta baseline still
            // advances only on ack (see HandleAckAsync), never on a send.
            MessageEnvelope envelope;
            string outType;
            if (message.IsKeyframe)
            {
                envelope = connection.BuildSnapshot(tick, entities, traceId);
                outType = nameof(MessageType.ServerSnapshot);
            }
            else
            {
                var removed = new List<string>(message.Removed.Count);
                foreach (var id in message.Removed)
                {
                    removed.Add(id.Value);
                }

                // A delta is taken against the viewer's acknowledged baseline tick — the last tick
                // the client confirmed. The client applies `changed`/`removed` on top of the state
                // it acked at `from` to reconstruct the full state at `to`.
                var fromTick = replicator.AcknowledgedTick(message.Viewer);
                envelope = connection.BuildDelta(fromTick, tick, entities, removed, traceId);
                outType = nameof(MessageType.ServerDelta);
            }

            switch (connection.Outbound.TryEnqueue(envelope))
            {
                case OutboundEnqueueResult.Enqueued:
                    _telemetry.Increment(TelemetryMetrics.MessagesOut, Tags(("messageType", outType)));
                    break;
                case OutboundEnqueueResult.DroppedQueueFull:
                    // The client is too far behind to keep up; shed this snapshot rather than
                    // block or buffer without bound. Observable as backpressure, never silent.
                    _telemetry.Increment(TelemetryMetrics.BackpressureRejections);
                    break;
                case OutboundEnqueueResult.DroppedClosed:
                    // The connection is no longer writable: drop it so it stops receiving fan-out.
                    dropped.Add(connection.Id);
                    _telemetry.Event(TelemetryEvents.ConnectionDropped,
                        Tags(("connectionId", connection.Id.Value), ("reason", "outbound_closed")));
                    break;
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
            case MessageType.ClientLeaveRoom:
                await HandleLeaveAsync(connection, inbound, cancellationToken).ConfigureAwait(false);
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

        var gameId = connection.Game ?? new GameId("unknown");
        var key = new RoomKey(session.Tenant.TenantId, join.RoomId);

        // Establish the room and this subscription atomically against teardown, so a join
        // can never interleave with a concurrent last-leaver reap (see OnDisconnect). A
        // join that would create a NEW room beyond the room ceiling is shed here.
        var admitted = false;
        var rejectReason = "max_rooms";
        // The game refused the join via its CanJoin rule (capacity/ban/phase). Distinct from a
        // platform capacity shed: it gets a definitive NotJoined error, not a retryable Overloaded.
        var gameRefused = false;
        lock (RoomLock(key))
        {
            var isNewRoom = !_subscribers.ContainsKey(key);
            // A join that would create a NEW room is checked against both the global ceiling and
            // the per-tenant ceiling, so one tenant cannot consume global room capacity and
            // starve the others (noisy-neighbour isolation). Joins into an already-running room
            // never count against a ceiling.
            if (isNewRoom && TenantRoomCount(session.Tenant.TenantId) >= _admission.MaxRoomsPerTenant)
            {
                rejectReason = "tenant_max_rooms";
            }
            else if (isNewRoom && _subscribers.Count >= _admission.MaxRooms)
            {
                rejectReason = "max_rooms";
            }
            else
            {
                var room = _router.GetOrCreateRoom(session.Tenant, join.RoomId, gameId);

                // The game's own admission rule runs before any membership is granted. A refusal
                // leaves NO membership, NO subscriber, NO replicator. If the room was created just
                // now solely for this join, tear it back down so a refused join cannot leak a room.
                bool canJoin;
                lock (room)
                {
                    canJoin = room.CanJoin(player);
                    if (canJoin)
                    {
                        room.Join(player);
                    }
                }

                if (!canJoin)
                {
                    gameRefused = true;
                    if (isNewRoom)
                    {
                        room.Terminate();
                        _router.TryRemoveRoom(key);
                    }
                }
                else
                {
                    _subscribers.GetOrAdd(key, _ => new ConcurrentDictionary<ConnectionId, Connection>())[connection.Id] = connection;
                    var replicator = _replicators.GetOrAdd(key, _ => new Replicator(_policyProvider(gameId)));
                    // A (re)joining connection cannot be assumed to hold any prior baseline: reset
                    // it so the next tick re-establishes a full keyframe for this viewer.
                    replicator.Resubscribe(new ViewerId(connection.Id.Value));
                    connection.JoinRoom(key, player);
                    admitted = true;
                }
            }
        }

        if (gameRefused)
        {
            // A definitive refusal by the game's rules (not retryable platform backpressure).
            _telemetry.Event(TelemetryEvents.CommandRejected, Tags(
                ("code", ServerErrorCode.NotJoined.ToString()), ("roomId", join.RoomId.Value), ("playerId", player.Value)));
            await RejectAsync(connection, inbound, ServerErrorCode.NotJoined,
                "The game refused this join.", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!admitted)
        {
            _telemetry.Increment(TelemetryMetrics.AdmissionRejected, Tags(("reason", rejectReason)));
            await RejectAsync(connection, inbound, ServerErrorCode.Overloaded,
                "Server is at room capacity; retry later.", cancellationToken).ConfigureAwait(false);
            return;
        }

        _telemetry.Event(TelemetryEvents.RoomJoined, Tags(("roomId", join.RoomId.Value), ("playerId", player.Value)));

        // Announce the join to the room as a discrete platform event. Emitted to this connection
        // so a just-joined client gets an immediate, observable lifecycle signal.
        await EmitEventAsync(connection, ServerEvent.Types.PlayerJoined, player, inbound.TraceId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a discrete <see cref="ServerEvent"/> to a connection (player joined/left, room
    /// terminating). The payload is the UTF-8 player id, so a client can attribute the event.
    /// Best-effort and non-blocking via the outbound buffer; lifecycle signalling must never
    /// stall the caller.
    /// </summary>
    private Task EmitEventAsync(Connection connection, string eventType, PlayerId player, string traceId, CancellationToken cancellationToken)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(player.Value);
        var envelope = connection.BuildServerMessage(MessageType.ServerEvent, new ServerEvent(eventType, payload), traceId);
        connection.Outbound.TryEnqueue(envelope);
        _telemetry.Increment(TelemetryMetrics.MessagesOut, Tags(("messageType", nameof(MessageType.ServerEvent))));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles a <see cref="MessageType.ClientLeaveRoom"/>: removes the player from its room,
    /// fires the game's <c>OnLeave</c>, drops the viewer's replication baseline, and emits a
    /// <see cref="ServerEvent"/> — all WITHOUT closing the connection (the client may rejoin).
    /// Under <see cref="RoomLifecycle.Reap"/> a now-empty room is torn down (with
    /// <c>OnTerminate</c>), exactly as a disconnect of the last subscriber would.
    /// </summary>
    private async Task HandleLeaveAsync(Connection connection, MessageEnvelope inbound, CancellationToken cancellationToken)
    {
        if (connection.RoomKey is not { } key || connection.Player is not { } player)
        {
            await RejectAsync(connection, inbound, ServerErrorCode.NotJoined, "Not in a room.", cancellationToken).ConfigureAwait(false);
            return;
        }

        lock (RoomLock(key))
        {
            if (_router.TryGetRoom(key, out var room))
            {
                lock (room)
                {
                    room.Leave(player);
                }
            }

            if (_subscribers.TryGetValue(key, out var connections))
            {
                connections.TryRemove(connection.Id, out _);

                if (_replicators.TryGetValue(key, out var replicator))
                {
                    replicator.Resubscribe(new ViewerId(connection.Id.Value));
                }

                // Reap a room emptied by an explicit leave, same as a last-subscriber disconnect.
                if (_lifecycle == RoomLifecycle.Reap && connections.IsEmpty)
                {
                    _subscribers.TryRemove(key, out _);
                    _replicators.TryRemove(key, out _);
                    if (_router.TryGetRoom(key, out var reapedRoom))
                    {
                        reapedRoom.Terminate();
                    }

                    _router.TryRemoveRoom(key);
                    _roomDirectory?.Release(key, _localNode);
                    _telemetry.Event(TelemetryEvents.RoomClosed, Tags(("roomId", key.RoomId.Value), ("tenantId", key.TenantId.Value)));
                }
            }
        }

        _telemetry.Event(TelemetryEvents.RoomLeft, Tags(("roomId", key.RoomId.Value), ("playerId", player.Value)));

        // Tell the (still-connected) client its leave took effect, while the connection still
        // carries the room/player context on the envelope, then clear membership.
        await EmitEventAsync(connection, ServerEvent.Types.PlayerLeft, player, inbound.TraceId, cancellationToken)
            .ConfigureAwait(false);
        connection.LeaveRoom();
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

        // Kernel-level inbound size bound (defense-in-depth behind the transport frame cap): a
        // single oversized command is shed with a typed error and the connection SURVIVES — one bad
        // frame must not drop the socket. Counted as an invalid message, never silent.
        if (_maxCommandBytes > 0)
        {
            var commandBytes = System.Text.Encoding.UTF8.GetByteCount(command.Command);
            if (commandBytes > _maxCommandBytes)
            {
                await RejectAsync(connection, inbound, ServerErrorCode.MalformedMessage,
                    $"Command payload of {commandBytes} bytes exceeds the {_maxCommandBytes}-byte limit.", cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        // Per-tenant rate limiting: meter this command against the sender's tenant token bucket
        // BEFORE it reaches the room queue. Over its fair share, the tenant's command is shed as
        // backpressure (Overloaded → BACKPRESSURE_REJECTED on the wire) without consuming any room
        // capacity — and, critically, without touching any OTHER tenant's bucket, so a flood by one
        // tenant cannot raise another tenant's reject rate. The session's resolved tenant is
        // authoritative (never the client's declared envelope tenant).
        if (_rateLimiter is not null && !_rateLimiter.TryAcquire(session.Tenant.TenantId))
        {
            _telemetry.Increment(TelemetryMetrics.BackpressureRejections);
            await RejectAsync(connection, inbound, ServerErrorCode.Overloaded,
                "Tenant is over its command rate; retry shortly.", cancellationToken).ConfigureAwait(false);
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
            case CommandAdmission.RejectedOverloaded:
                // Backpressure, not a client error: the room queue is full. Tell the
                // client to retry shortly and count it as a shed under load, separately
                // from protocol/validation rejections, so overload is visible on its own.
                _telemetry.Increment(TelemetryMetrics.BackpressureRejections);
                await RejectAsync(connection, inbound, ServerErrorCode.Overloaded,
                    "Room is shedding load; retry shortly.", cancellationToken).ConfigureAwait(false);
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
        // Per-connection outbound buffer depth. A slow client may fall this many snapshots behind
        // before fan-out starts dropping its messages (it is never allowed to block the tick).
        private const int OutboundCapacity = 256;

        private long _outboundSequence;

        public Connection(IServerPushTransport transport, JoinTokenClaims principal)
        {
            Transport = transport;
            Principal = principal;
            Outbound = new BoundedOutboundChannel(transport, OutboundCapacity);
        }

        public IServerPushTransport Transport { get; }

        /// <summary>This connection's non-blocking outbound buffer; fan-out enqueues here, never awaiting the socket.</summary>
        public IOutboundChannel Outbound { get; }

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

        /// <summary>Clears room membership after an explicit leave; the connection stays open and may rejoin.</summary>
        public void LeaveRoom()
        {
            RoomKey = null;
            Player = null;
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

        /// <summary>
        /// Builds one <see cref="ServerDelta"/> envelope: the entities that changed (and the
        /// ids that left this viewer's view) between the viewer's acknowledged baseline tick
        /// <paramref name="fromTick"/> and <paramref name="toTick"/>. The client applies it on
        /// top of the state it acked at <paramref name="fromTick"/>.
        /// </summary>
        public MessageEnvelope BuildDelta(
            long fromTick, long toTick, IReadOnlyList<EntityState> changed, IReadOnlyList<string> removed, string traceId) =>
            new()
            {
                TenantId = TenantId ?? new TenantId("unknown"),
                GameId = Game ?? new GameId("unknown"),
                RoomId = RoomKey?.RoomId,
                SessionId = Session?.Id,
                PlayerId = Player,
                ProtocolVersion = ProtocolVersions.Current,
                MessageType = MessageType.ServerDelta,
                Sequence = Interlocked.Increment(ref _outboundSequence),
                TraceId = traceId,
                Payload = new ServerDelta(fromTick, toTick, changed, removed),
            };
    }
}
