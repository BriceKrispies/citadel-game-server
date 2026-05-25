# Architecture

The system separates a **control plane** from a **realtime data plane**. The data
plane is a deterministic kernel; everything else is replaceable infrastructure.

## Control plane vs realtime data plane

### Control plane (not in the hot path)
Owns tenant registry, provisioning, tenant→database mapping, feature flags, the
game catalog, protocol version policy, quotas, admin APIs, and audit records. It
configures and provisions the system; it never handles realtime traffic.

> Status: `GameServer.ControlPlane` now provides in-memory game catalog, room
> registry, join-token service, and session lifecycle behind the HTTP API.

### Realtime data plane (the hot path)
Owns connections, authentication handoff, protocol negotiation, session routing,
room placement, authoritative simulation, command validation, snapshots, fanout,
backpressure, and telemetry. It must stay boring, fast, observable, and testable.

## Public API boundary

### Control-plane API boundary (HTTP / JSON)
`GameServer.Host` exposes the control-plane HTTP API (`/health`, `/version`,
`/api/v1/games`, `/api/v1/rooms`, `/api/v1/rooms/{id}/join-token`,
`/api/v1/sessions`). These endpoints are thin delegations to `GameServer.ControlPlane`
services and return stable DTOs / typed errors. They perform **session and lifecycle
management only** and never touch the authoritative simulation: creating a room here
records metadata; it does not create or run a simulation room.

### Realtime API boundary (WebSocket / binary protobuf)
`GET /realtime/v1/connect?joinToken=…` is the gameplay transport. It requires a join
token, negotiates protocol version, and accepts **binary protobuf frames only**
(text frames are rejected with a typed `ServerError`). The endpoint wraps the socket
in `AspNetRealtimeChannel`, adapts it via `ProtobufRealtimeTransport` (which uses
`RealtimeProtobufCodec` + `RealtimeEnvelopeMapper`), and routes accepted messages
into the unchanged `RealtimeServer` kernel. The kernel's domain envelope is mapped
to/from the wire `RealtimeEnvelope` at this edge, so the simulation stays free of
protobuf and transport concerns.

### Protobuf as the canonical wire contract
`contracts/realtime/proto/gameserver.realtime.v1.proto` is the single source of truth
for the realtime protocol; it is compiled into `GameServer.Protocol`. Browser clients
implement against it. JSON is a development-only debug codec (the `/ws` path, enabled
only in Development) — never the contract. There is no SignalR.

### Why gameplay does not use REST
REST is request/response and stateless per call; gameplay is a long-lived, stateful,
bidirectional, latency-sensitive stream with server authority, sequencing, prediction,
and corrections. Putting input on REST endpoints would lose ordering, add per-message
overhead, and break the authoritative tick model. So REST is confined to control-plane
lifecycle, and all gameplay intent/state flows over the realtime WebSocket.

### How browser clients are expected to communicate
1. Call the HTTP control plane to discover/create a room and obtain a `JoinTokenContract`.
2. Open a WebSocket to `/realtime/v1/connect?joinToken=…`.
3. Send a binary protobuf `ClientHello`; receive `ServerWelcome`.
4. Send `ClientJoinRoom`, then stream `ClientInputFrame` / `ClientCommand` / `ClientAck`
   / `ClientPing` as binary frames; predict locally.
5. Receive authoritative `ServerSnapshot` / `ServerDelta` / `ServerCorrection` /
   `ServerEvent` / `ServerPong`; reconcile predictions to corrections.
6. Game-specific payloads ride the `GameMessage` extension; unknown ones fail
   explicitly with `UNKNOWN_GAME_MESSAGE`.

## Hot path

```
receive command            IClientCommandReceiver (transport edge)
  → decode/validate         RealtimeServer.ProcessAsync + Protocol contracts
  → resolve session         ISessionRouter (tenant carried explicitly)
  → route to room           RoomKey = (TenantId, RoomId)  ← isolation is structural
  → enqueue command         IGameRoom.TryEnqueue (per-player sequence check)
  → apply on tick           IGameRoom.Tick (ISimulationClock advances)
  → advance authoritative   room mutates its own state, only here
  → persist snapshot/events ISnapshotStore / IEventLog
  → fan out snapshot        IServerPushTransport.SendAsync (per-client projection)
  → record telemetry        ITelemetrySink
```

Tenant context is resolved once at the edge (`ClientHello`) and carried on the
session — never re-resolved inside the loop, never ambient/global.

### Why the abstractions are split the way they are
- `IServerPushTransport` / `IClientCommandReceiver` / `IBidirectionalTransport`:
  an SSE/spectator transport can push but cannot receive. Splitting the contracts
  means a push-only transport is never forced to implement (and lie about) a
  receive method. WebSocket implements the bidirectional contract; SSE implements
  push-only. This is verified by `TransportContractTests`.
- The room returns a `TickResult` (snapshot + events) instead of writing to stores,
  so the simulation has zero persistence/observability dependencies.
- Wire snapshots (`ServerSnapshot`) are a per-client projection of the room's full
  authoritative `RoomSnapshot`, keeping the protocol decoupled from sim internals.

## Current test harness

Tests are co-located beside the production code (e.g. `GameRoom.Tests.cs` next to
`GameRoom.cs`) but compile into the `GameServer.Tests` assembly only. Cross-feature
test support lives in `Testing/` folders within the relevant source project:

- In-memory production defaults (also usable as honest fakes): `InMemoryTenantResolver`,
  `InMemorySessionRouter`, `InMemoryBidirectionalTransport`, `BufferedServerPushTransport`
  (SSE-like), `InMemorySnapshotStore`, `InMemoryEventLog`.
- `src/GameServer.Simulation/Testing/`: `FakeSimulationClock` (manual ticks),
  `DeterministicRandomSource` (seeded).
- `src/GameServer.Observability/Testing/`: `TestTelemetrySink` (records all emissions).
- `src/GameServer.Transport/Testing/`: `FakeClient` (scripts envelopes) and
  `SliceHarness` (wires the kernel).

No test uses `Thread.Sleep`, `Task.Delay`, `DateTime.UtcNow`, `Random.Shared`,
sockets, databases, or AWS. Determinism comes from completing channels and
manually-driven ticks, not timing.

The co-located convention is enforced by the `CITADEL0001` Roslyn analyzer
(`tools/RepoAnalyzers`), configured by `repo-analyzers.json`. Mutation testing is
available via Stryker.NET (`stryker-config.json`) as deeper validation. See the
README for all validation commands.

Run (everything): `dotnet test Citadel.slnx`.

## Planned next milestones

1. **Tick loop / room runner**: drive `TickRoom` on a cadence behind a deterministic
   driver; add `missed_ticks` / `tick_duration_ms` telemetry at the edge (wall-clock
   stays out of the room).
2. **Deltas & corrections**: emit `ServerDelta` and `ServerCorrection`; add divergence
   detection.
3. **Reconnect & recovery**: restore a room from snapshot + event-log replay.
4. **Backpressure**: bounded transport channels; reject/shed cleanly under overload;
   `backpressure_rejections` telemetry; load scenarios in `GameServer.LoadHarness`.
5. **Control plane**: real tenant registry/provisioning, protocol-version policy,
   quotas, audit — kept out of the hot path.
6. **Production transports**: WebSocket (`IBidirectionalTransport`) and SSE
   (`IServerPushTransport`) over ASP.NET Core, swapped in without touching the kernel.
7. **Persistence backends**: durable snapshot/event stores behind the existing
   generic contracts.

## Horizontal scale: room ownership across a fleet

A room is a single-owner authoritative actor, so a fleet must agree on exactly one owning node per
`RoomKey` or it split-brains (the same room ticking on two nodes — pinned by
`MultiNodeOwnershipScenario`). Three Routing ports cover this, each with a backend-agnostic contract:

- **`IRoomDirectory`** — source of truth for room→owner. Atomic fenced claim, owner-gated release,
  per-owner counts (`OwnedCount`). `NodeId.Value` is the node's reachable base address.
- **`IRoomPlacement`** — assigns a room to one node, idempotently and capacity-aware, returning
  `ClusterAtCapacity` when every node is full. Writes the directory.
- **`IRoomAffinityRouter`** — resolves, for the local node, whether to serve a room or redirect to its
  owner.

Backends mirror the InMemory↔durable split used for snapshot stores:

- `InMemoryRoomDirectory` / `CapacityAwareRoomPlacement` (Routing) — thread-safe, single-process
  authoritative; used for a single node, for dev, and for tests that share one instance to model shared
  infrastructure.
- `RedisRoomDirectory` / `RedisRoomPlacement` (`GameServer.Cluster.Redis`) — real cross-node ownership:
  fenced leased keys (`SET … NX PX`), per-node room sets scored by lease-expiry so counts self-exclude a
  dead node's claims, and a single Lua script for atomic capacity-checked placement. Proven by
  `RedisRoomDirectoryScenario` (Testcontainers; skipped without Docker).

The Host (`Program.cs`) selects the backend by config (`Cluster:Backend` = InMemory|Redis, plus
`Cluster:NodeId`, `Cluster:Nodes`, `Cluster:MaxRoomsPerNode`). Room creation places the room (`503` if
the cluster is full); `/realtime/v1/connect` consults affinity before accepting the socket and answers
`409` with the owner's address when a connection lands on a non-owner, so a non-owner never stands up a
second copy of a room. End-to-end behavior is pinned by `ClusterRoutingHostScenario` (two Hosts sharing
one directory).
