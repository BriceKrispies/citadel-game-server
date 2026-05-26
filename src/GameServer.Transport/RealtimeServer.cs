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
    // The richer capabilities of the same injected stores, when wired (the in-memory/file/Postgres
    // stores all implement these). Non-null enables checkpoint history + rewind: TickRoom writes
    // periodic checkpoints and prunes to the horizon, and RewindRoom restores a past tick and forks
    // the timeline. Null (a latest-only store, e.g. a minimal unit harness) keeps the legacy
    // save-every-tick behavior and makes rewind unavailable.
    private readonly ISnapshotHistoryStore<RoomKey, RoomSnapshot>? _snapshotHistory;
    private readonly IRewindableEventLog<RoomKey, RoomEvent>? _rewindableEvents;
    // How often (in ticks) a durable checkpoint is written when a history store is wired. 1 = every
    // tick (finest rewind granularity); larger spaces checkpoints out to trade rewind precision for
    // fewer writes (replay covers the gap from the nearest earlier checkpoint).
    private readonly int _checkpointEveryTicks;
    private readonly ITelemetrySink _telemetry;
    private readonly Func<GameId, ReplicationPolicy> _policyProvider;
    private readonly RoomLifecycle _lifecycle;
    // The admission ceilings. Mutable so an authorized platform admin can re-tighten/loosen capacity at
    // runtime (the limits-config endpoint) without a redeploy. Reads/writes are guarded by _admissionLock,
    // the same lock that serializes the admission counters, so a ceiling change is consistent with the
    // count it is compared against.
    private AdmissionPolicy _admission;
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

    // Graceful-degradation ladder. Null when not configured (unit harnesses): then the server runs at
    // full service. When wired (the deployed host — see Program.cs), the tick driver feeds it the
    // observed tick health (missed_ticks / tick p95) and the EDGE consults its current level ON the hot
    // path to shed OPTIONAL load before authoritative correctness is at risk: at RejectNewConnections it
    // refuses new connections, at RejectNewRooms it refuses joins that would CREATE a new room, at
    // ShedTelemetry it drops the non-critical per-message structured events, and at
    // ReduceSpectatorSnapshots it thins the observer/spectator push (see ShouldEmitSpectatorSnapshot).
    // It NEVER skips the authoritative tick, state mutation, or snapshot/event persistence: correctness
    // outranks accepting load. Wired only at the composition root.
    private readonly IDegradationController? _degradation;

    // Admission counters. Guarded by _admissionLock (per-connection, not on the hot path).
    private readonly object _admissionLock = new();
    private readonly Dictionary<string, int> _connectionsPerTenant = new();
    private int _connectionCount;

    private readonly ConcurrentDictionary<RoomKey, ConcurrentDictionary<ConnectionId, Connection>> _subscribers = new();
    private readonly ConcurrentDictionary<RoomKey, Replicator> _replicators = new();
    // The game placed in each live room, recorded at creation and removed at teardown (via RemoveRoom).
    // Rewind needs it to rebuild the room through the replay engine's game factory; the room itself does
    // not carry its GameId.
    private readonly ConcurrentDictionary<RoomKey, GameId> _roomGames = new();
    // The deterministic replay engine, when wired. Non-null (with the history + rewindable stores) is
    // what enables RewindRoom; null leaves rewind unavailable. Wired only at the composition root.
    private readonly RoomReplayService? _replay;
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
        int maxCommandBytes = 0,
        IDegradationController? degradation = null,
        int checkpointEveryTicks = 1,
        RoomReplayService? replay = null)
    {
        _tenants = tenants;
        _roomDirectory = roomDirectory;
        _localNode = localNode;
        _rateLimiter = rateLimiter;
        _tenantMetrics = tenantMetrics;
        _maxCommandBytes = maxCommandBytes;
        _degradation = degradation;
        _router = router;
        _snapshots = snapshots;
        _events = events;
        // Same instances, surfaced through their richer ports when supported (LSP: a history store IS-A
        // snapshot store, a rewindable log IS-A log). Captured once so the hot path does no repeated casts.
        _snapshotHistory = snapshots as ISnapshotHistoryStore<RoomKey, RoomSnapshot>;
        _rewindableEvents = events as IRewindableEventLog<RoomKey, RoomEvent>;
        _checkpointEveryTicks = checkpointEveryTicks > 0 ? checkpointEveryTicks : 1;
        _replay = replay;
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
    public AdmissionPolicy Admission
    {
        get { lock (_admissionLock) { return _admission; } }
    }

    /// <summary>
    /// Replaces the admission ceilings at runtime (the platform-admin limits-config endpoint). Guarded by
    /// the admission lock so the new ceilings are immediately consistent with the live counts they gate.
    /// Tightening below the current count does not evict existing connections/rooms — it simply stops
    /// admitting NEW ones until the live count falls back under the ceiling (graceful, never a forced drop).
    /// </summary>
    public void UpdateAdmission(AdmissionPolicy policy)
    {
        lock (_admissionLock)
        {
            _admission = policy;
        }
    }

    /// <summary>
    /// The current graceful-degradation level the data plane is shedding at (<see cref="DegradationLevel.Normal"/>
    /// when no controller is wired). The tick driver feeds the controller; the edge reads this to shed optional
    /// load. Exposed so operators (and scenarios) can observe the ladder position without reaching into the
    /// controller.
    /// </summary>
    public DegradationLevel DegradationLevel => _degradation?.Current ?? DegradationLevel.Normal;

    /// <summary>
    /// Whether an OPTIONAL spectator/observer push (e.g. the admin "drop-in" observe stream) should emit on
    /// the given monotonically increasing stream tick. At <see cref="DegradationLevel.ReduceSpectatorSnapshots"/>
    /// or higher the spectator rate is halved (emit on even ticks only) to reclaim fan-out budget; below it,
    /// every tick emits. This NEVER affects authoritative players — it only thins the non-authoritative
    /// observer push, the cheapest, most optional work, shed first on the ladder.
    /// </summary>
    public bool ShouldEmitSpectatorSnapshot(long streamTick) =>
        DegradationLevel < DegradationLevel.ReduceSpectatorSnapshots || (streamTick % 2) == 0;

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

        // The connection's stable correlation id — the single thread that ties this connection's
        // whole lifecycle together in structured logs/telemetry: connect → hello → join → command →
        // fanout-on-its-command → error all carry it as the `correlationId` field. It is independent
        // of the per-message `traceId` (which a client mints per request and the server echoes on the
        // matching response): the correlation id is per-CONNECTION and server-owned, so a hostile or
        // sloppy client cannot fragment or collide a session's trail. Seeded from the connection id
        // (globally unique at the edge); adopted from the client's hello traceId only if it supplies a
        // non-empty one, so a client that already has a request id can carry it through.

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
        _telemetry.Event(TelemetryEvents.ConnectionOpened, CorrelatedTags(connection));

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
                    _telemetry.Event(TelemetryEvents.ConnectionDropped, CorrelatedTags(connection,
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
        // Graceful-degradation last rung: under sustained overload the ladder refuses NEW connections
        // (existing ones keep being served) so the tick loop can reclaim headroom before authoritative
        // correctness is at risk. Read on the hot connect path; a no-op at every level below the top.
        if (DegradationLevel >= DegradationLevel.RejectNewConnections)
        {
            reason = "degraded_reject_connections";
            return false;
        }

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
                _roomGames.TryRemove(key, out _);
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
    /// Administratively terminates a live room through the AUTHORITATIVE pathway — the same teardown a
    /// last-subscriber reap performs, never a side mutation of room state. Under the room lock (so it can
    /// never interleave with a tick/join/leave) it fires the game's <c>OnTerminate</c> hook, drops the
    /// subscriber set, replicator and routed room, closes each connection's outbound buffer, and releases
    /// the cluster ownership claim. After it returns the room leaves <see cref="ActiveRooms"/> and
    /// <see cref="TryObserveRoom"/> reports it gone — no orphan state. Returns false if the room was not
    /// active. Observable: emits <see cref="TelemetryEvents.RoomClosed"/> tagged with the operator reason.
    /// </summary>
    public bool TerminateRoom(RoomKey key, string reason)
    {
        lock (RoomLock(key))
        {
            // Nothing to terminate if the room is not currently placed. Idempotent for a second call.
            var hadSubscribers = _subscribers.TryRemove(key, out var connections);
            var hadRoom = _router.TryGetRoom(key, out var room);
            if (!hadSubscribers && !hadRoom)
            {
                return false;
            }

            // Authoritative teardown: the game finalizes its own state via OnTerminate. The room owns the
            // mutation; the lock serializes it against the tick driver. Only the room mutates the room.
            if (hadRoom)
            {
                lock (room!)
                {
                    room.Terminate();
                }
            }

            // Release each viewer's outbound buffer (and any background writer) so the connection's send
            // loop completes and the socket is not left dangling.
            if (connections is not null)
            {
                foreach (var connection in connections.Values)
                {
                    _ = connection.Outbound.DisposeAsync();
                }
            }

            _replicators.TryRemove(key, out _);
            _router.TryRemoveRoom(key);
            _roomGames.TryRemove(key, out _);
            // Relinquish the cluster ownership claim so the directory's OwnedCount tracks live rooms and
            // another node may later take this room. No-op when clustering is not wired.
            _roomDirectory?.Release(key, _localNode);
            _telemetry.Event(TelemetryEvents.RoomClosed,
                Tags(("roomId", key.RoomId.Value), ("tenantId", key.TenantId.Value), ("reason", reason)));
            return true;
        }
    }

    /// <summary>
    /// Rewinds a live room's authoritative state to <paramref name="targetTick"/> and resumes it on a
    /// FORKED timeline — the events after the target are discarded. Under the room lock (so it never
    /// interleaves with the swapped room's tick/join/leave) it: rebuilds the state as of the target via
    /// the deterministic replay engine (a sandbox room), atomically swaps that into the router, persists
    /// a fresh checkpoint at the target and drops events after it, and resets each connected viewer's
    /// replication baseline so the next tick sends a full keyframe — the correction that snaps clients
    /// onto the rewound state (stale client command sequences were cleared by the restore). Returns the
    /// outcome; emits <see cref="TelemetryEvents.RoomRewound"/> on success.
    /// </summary>
    /// <remarks>
    /// Requires the replay engine and the history + rewindable stores to be wired (otherwise
    /// <see cref="RoomRewindOutcome.NotRewindable"/>). The CALLER must ensure the room is not being
    /// ticked concurrently for the duration (the bulk coordinator holds a tick pause gate; a manual
    /// driver must be quiesced) — the room lock serializes against admission/join, not against an
    /// already-in-flight tick that fetched the prior room instance.
    /// </remarks>
    public RoomRewindOutcome RewindRoom(RoomKey key, long targetTick, string reason)
    {
        if (_replay is null || _snapshotHistory is null || _rewindableEvents is null)
        {
            return RoomRewindOutcome.NotRewindable;
        }

        lock (RoomLock(key))
        {
            if (!_router.TryGetRoom(key, out var current) || !_roomGames.TryGetValue(key, out var gameId))
            {
                return RoomRewindOutcome.NotFound;
            }

            // Rebuild state as of the target tick in a sandbox (never registered). Serialize against an
            // in-flight tick/command on the current instance while we rebuild and swap.
            RoomReplayResult replay;
            lock (current)
            {
                replay = _replay.ReplayTo(key, gameId, targetTick);
                switch (replay.Outcome)
                {
                    case RoomReplayOutcome.BeyondHorizon:
                        return RoomRewindOutcome.BeyondHorizon;
                    case RoomReplayOutcome.Failed:
                        return RoomRewindOutcome.Failed;
                }

                // Swap the rebuilt room in atomically, then fork the timeline: a fresh checkpoint AT the
                // target becomes the new restore base, and every event after the target is discarded so
                // the authoritative log has no invalid future.
                _router.TryReplaceRoom(key, replay.Room!);
                _snapshotHistory.Save(key, replay.Room!.Snapshot());
                _rewindableEvents.DiscardAfter(key, targetTick);
            }

            // Correct connected clients: reset each viewer's delta baseline so the next tick re-establishes
            // a full keyframe carrying the rewound state, which the client replaces its view with.
            if (_replicators.TryGetValue(key, out var replicator) &&
                _subscribers.TryGetValue(key, out var connections))
            {
                foreach (var connection in connections.Values)
                {
                    replicator.Resubscribe(new ViewerId(connection.Id.Value));
                }
            }

            _telemetry.Increment(TelemetryMetrics.RoomRewindCount,
                Tags(("roomId", key.RoomId.Value), ("tenantId", key.TenantId.Value)));
            _telemetry.Event(TelemetryEvents.RoomRewound,
                Tags(("roomId", key.RoomId.Value), ("tenantId", key.TenantId.Value),
                    ("toTick", targetTick.ToString()), ("reason", reason)));
            return RoomRewindOutcome.Rewound;
        }
    }

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

        // Persist authoritative state. With a history store wired, write a CHECKPOINT on the configured
        // cadence — these accumulate as the rewind history. Otherwise keep the latest-snapshot behavior.
        if (_snapshotHistory is not null)
        {
            if (_checkpointEveryTicks <= 1 || result.Snapshot.Tick % _checkpointEveryTicks == 0)
            {
                _snapshotHistory.Save(key, result.Snapshot);
            }
        }
        else
        {
            _snapshots.Save(key, result.Snapshot);
        }

        foreach (var roomEvent in result.Events)
        {
            _events.Append(key, roomEvent);
        }

        if (_eventLogRetentionTicks > 0)
        {
            PruneToHorizon(key, result.Snapshot.Tick);
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
        // The per-snapshot structured event is NON-CRITICAL diagnostic detail (the counter above is the
        // SLO-bearing signal and is always emitted). Under ShedTelemetry+ the ladder drops it to reclaim
        // CPU on the hottest path; authoritative state and the snapshot itself are unaffected.
        EmitNonCriticalEvent(TelemetryEvents.SnapshotEmitted, Tags(("roomId", key.RoomId.Value), ("tick", result.Snapshot.Tick.ToString())));
    }

    /// <summary>
    /// Bounds persisted history to the rewind horizon. With a history store wired, it drops checkpoints
    /// older than the floor at the horizon (keeping that floor as the restore base) and then drops events
    /// already folded into that floor — so any tick in the horizon window stays replayable while the log
    /// and history cannot grow without bound. With only a latest-only store, it keeps the original
    /// behavior: the saved snapshot covers everything through the current tick, so older events compact away.
    /// </summary>
    private void PruneToHorizon(RoomKey key, long currentTick)
    {
        var watermark = currentTick - _eventLogRetentionTicks;

        if (_snapshotHistory is not null && _rewindableEvents is not null)
        {
            _snapshotHistory.PruneThrough(key, watermark);
            if (_snapshotHistory.TryGetLatestAtOrBefore(key, watermark, out var floor))
            {
                // Events at or below the retained floor checkpoint are folded into it (replay starts
                // strictly after the checkpoint tick), so they are safe to compact away.
                _rewindableEvents.TruncateThrough(key, floor.Tick);
            }

            return;
        }

        _events.TruncateThrough(key, watermark);
    }

    /// <summary>
    /// Emits a NON-CRITICAL structured event unless the degradation ladder is at
    /// <see cref="DegradationLevel.ShedTelemetry"/> or higher, in which case it is dropped to reclaim CPU
    /// under sustained overload. SLO-bearing counters/measures are emitted unconditionally elsewhere; only
    /// the optional per-message diagnostic events go through here, so shedding telemetry never blinds the
    /// core metrics and never touches authoritative simulation.
    /// </summary>
    private void EmitNonCriticalEvent(string name, IReadOnlyDictionary<string, string> fields)
    {
        if (DegradationLevel >= DegradationLevel.ShedTelemetry)
        {
            return;
        }

        _telemetry.Event(name, fields);
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
            _telemetry.Event(TelemetryEvents.IdentityRejected, CorrelatedTags(connection,
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
            _telemetry.Event(TelemetryEvents.ProtocolRejected, CorrelatedTags(connection, ("requested", inbound.ProtocolVersion.ToString())));
            await RejectAsync(connection, inbound, ServerErrorCode.UnsupportedProtocolVersion,
                $"Protocol version {inbound.ProtocolVersion} is not supported.", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!_tenants.TryResolve(inbound.TenantId, out var tenant))
        {
            _telemetry.Event(TelemetryEvents.TenantRejected, CorrelatedTags(connection, ("tenantId", inbound.TenantId.Value)));
            await RejectAsync(connection, inbound, ServerErrorCode.UnknownTenant,
                $"Unknown tenant '{inbound.TenantId}'.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var session = _router.CreateSession(tenant);
        connection.Session = session;
        _telemetry.Event(TelemetryEvents.SessionCreated, CorrelatedTags(connection,
            ("sessionId", session.Id.Value), ("tenantId", tenant.TenantId.Value)));

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
            _telemetry.Event(TelemetryEvents.IdentityRejected, CorrelatedTags(connection,
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
        // Snapshot the (mutable) admission ceilings once so a concurrent runtime update cannot make the
        // two checks below disagree with each other mid-join.
        var admission = Admission;
        lock (RoomLock(key))
        {
            var isNewRoom = !_subscribers.ContainsKey(key);
            // Graceful-degradation rung below refusing connections: under overload the ladder refuses
            // joins that would CREATE a NEW room (existing rooms keep ticking and accepting rejoins), so
            // the tick loop stops taking on more authoritative work while it is already over budget.
            // Joins into an already-running room are never refused by degradation — only new load is.
            if (isNewRoom && DegradationLevel >= DegradationLevel.RejectNewRooms)
            {
                rejectReason = "degraded_reject_rooms";
            }
            // A join that would create a NEW room is checked against both the global ceiling and
            // the per-tenant ceiling, so one tenant cannot consume global room capacity and
            // starve the others (noisy-neighbour isolation). Joins into an already-running room
            // never count against a ceiling.
            else if (isNewRoom && TenantRoomCount(session.Tenant.TenantId) >= admission.MaxRoomsPerTenant)
            {
                rejectReason = "tenant_max_rooms";
            }
            else if (isNewRoom && _subscribers.Count >= admission.MaxRooms)
            {
                rejectReason = "max_rooms";
            }
            else
            {
                var room = _router.GetOrCreateRoom(session.Tenant, join.RoomId, gameId);
                // Remember the room's game so a later rewind can rebuild it through the replay factory
                // (the room does not carry its GameId). Cleared in RemoveRoom on teardown.
                _roomGames[key] = gameId;

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
                        _roomGames.TryRemove(key, out _);
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
            _telemetry.Event(TelemetryEvents.CommandRejected, CorrelatedTags(connection,
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

        _telemetry.Event(TelemetryEvents.RoomJoined, CorrelatedTags(connection,
            ("roomId", join.RoomId.Value), ("playerId", player.Value)));

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
                    _roomGames.TryRemove(key, out _);
                    _roomDirectory?.Release(key, _localNode);
                    _telemetry.Event(TelemetryEvents.RoomClosed, Tags(("roomId", key.RoomId.Value), ("tenantId", key.TenantId.Value), ("traceId", inbound.TraceId)));
                }
            }
        }

        _telemetry.Event(TelemetryEvents.RoomLeft, Tags(("roomId", key.RoomId.Value), ("playerId", player.Value), ("traceId", inbound.TraceId)));

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
                // The command leg of the correlation chain: an accepted intent is now an observable
                // structured event (previously only a counter), tagged with the connection's correlationId
                // and the request's traceId so connect → join → command is joinable end to end.
                _telemetry.Event(TelemetryEvents.CommandAccepted, CorrelatedTags(connection,
                    ("roomId", key.RoomId.Value), ("playerId", player.Value),
                    ("sequence", inbound.Sequence.ToString()), ("traceId", inbound.TraceId)));
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
        // The error leg of the correlation chain: carries the connection's stable correlationId AND the
        // per-message traceId, so an operator can both follow the whole session and pinpoint the request.
        _telemetry.Event(TelemetryEvents.CommandRejected, CorrelatedTags(connection,
            ("code", code.ToString()), ("traceId", inbound.TraceId)));

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

    /// <summary>
    /// Builds telemetry tags stamped with the connection's stable <c>correlationId</c> (and its
    /// <c>connectionId</c>), so every structured event for one connection is joinable by a single id
    /// from connect through error. Use this for any event raised while handling a known connection.
    /// </summary>
    private static IReadOnlyDictionary<string, string> CorrelatedTags(
        Connection connection, params (string Key, string Value)[] pairs)
    {
        var tags = new Dictionary<string, string>(pairs.Length + 2)
        {
            ["correlationId"] = connection.CorrelationId,
            ["connectionId"] = connection.Id.Value,
        };
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
            // Server-owned and stable for the connection's whole lifetime, fixed at connect (before any
            // client frame). Seeded from the globally-unique connection id. Deliberately NOT derived from
            // any client-supplied value, so a hostile/sloppy client cannot fragment or collide a session's
            // trail. The client's per-request id travels separately as the per-message traceId.
            CorrelationId = $"conn-{transport.ConnectionId.Value}";
        }

        /// <summary>
        /// The stable per-connection correlation id, attached to every structured event for this
        /// connection so an operator can follow one connection's whole trail with one id, from the
        /// connect event (before hello) through any error.
        /// </summary>
        public string CorrelationId { get; }

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
