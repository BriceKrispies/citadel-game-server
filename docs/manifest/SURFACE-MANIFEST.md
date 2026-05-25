# Citadel Surface Manifest

A code-graph map of **every exposed surface** of the game server — every place the outside
world (or an always-running internal loop) enters the system — traced from its entry point
inward to its natural authoritative **stopping point**.

- Machine-readable graph: [`surface-manifest.json`](./surface-manifest.json)
- Per-tracer fragments: [`fragments/`](./fragments/) (`tracer-r`, `tracer-c`, `tracer-w`, `tracer-x`)
- This file: the human index.

The manifest is the ground-truth model behind the `invariants` config block in
`repo-analyzers.json`, which the **CITADEL0004–0013** analyzers enforce at build time. When a
surface or invariant moves, update the fragment, re-synthesize, and adjust the config.

> **Provenance note.** A four-agent team traced these surfaces in parallel. Subagent file
> writes did not persist to the working tree, so the synthesizer re-authored the fragments
> from each agent's traced findings, reconciled against source. Tracer W's result was lost to
> an internal error and was re-traced directly from the worker source files.

## Stopping-point definition

A trace terminates at the first of:

1. **Authoritative kernel** — `GameRoom.Tick` / `IGameSimulation.Apply`
2. **Persistence / replication port** — `ISnapshotStore`, `IEventLog`, `IRoomReplicator`
3. **Registry / abstraction port** — `InMemory*Registry`, `IRoomDirectory`, `IRoomPlacement`, `IAuditLog`, `JoinTokenService`, `IGameCatalog`
4. **Telemetry sink** — `ITelemetrySink` / `AggregatingTelemetrySink`
5. **Process boundary** — stdout write (IPC) or Redis `EVAL`

A node that only calls ring-0 kernel ports (Abstractions / Protocol / Replication) is terminal.

## Surfaces

### Realtime data plane (Tracer R)

| Surface | Auth | Entry | Stops at | Invariants |
|---|---|---|---|---|
| `GET /realtime/v1/connect` (binary WS) | join-token | `RealtimeServer.HandleConnectionAsync` | `IGameSimulation.Apply` | tenant, backpressure, protocol, observability, authoritative-intent |
| `GET /ws` (text WS, dev) | none — gated by `IsDevelopment` **AND** `Realtime:EnableDevEndpoints` | `RealtimeServer.HandleConnectionAsync` | `IGameSimulation.Apply` | tenant, protocol, authoritative-intent |

Path: edge token-verify + affinity/placement → `AspNet*Channel` → `ProtobufRealtimeTransport`
→ `RealtimeProtobufCodec.Decode` (ring 0) → `RealtimeEnvelopeMapper.MapClient` →
`RealtimeServer.ProcessAsync` (typed `MessageType` dispatch) → `HandleHello/Join/Command/Ack`
→ `GameRoom.TryEnqueue` (validate, bounded queue) → `GameRoom.Tick` → `IGameSimulation.Apply`.

Key invariants: tenant from `Session.Tenant` (not the envelope); command path uses
`connection.Player` not `inbound.PlayerId`; `ClientInputFrame` is accepted-but-ignored intent.

### Control & admin plane (Tracer C)

| Surface | Auth | Stops at | Invariants |
|---|---|---|---|
| `POST /api/v1/rooms` | api-key | `IRoomPlacement.Place` (503 `ClusterAtCapacity` when full) | tenant, observability, authz, backpressure |
| `POST /api/v1/rooms/{roomId}/join-token` | api-key | `JoinTokenService.Issue` | tenant, observability, authz |
| `GET /api/v1/rooms/{roomId}` | api-key | `InMemoryRoomRegistry` (tenant from stored record) | tenant, authz |
| `DELETE /api/v1/sessions/{sessionId}` | api-key | `InMemorySessionRegistry` | tenant, observability, authz |
| `GET /api/v1/admin/rooms` | api-key + platform-admin | `RoomScopedMetrics.Hottest` | tenant, observability, authz |
| `GET /api/v1/admin/rooms/{tenantId}/{roomId}` | api-key | `RealtimeServer.TryObserveRoom` | tenant, authz |
| `GET …/{tenantId}/{roomId}/observe` (SSE) | api-key | `RealtimeServer.TryObserveRoom` | tenant, observability, authz |
| `GET /sim/telemetry` (SSE) | none — dev-only gate | `AggregatingTelemetrySink.Snapshot` | observability |
| `GET /health,/ready,/version` | none | `IRoomDirectory.TryGetOwner` (`/ready`) | — |

Auth seam: `/api/v1` group filter → `ApiKeyControlPlaneAuthenticator.TryAuthenticate` →
`CallerPrincipal`. Per-op `Forbid()` → `CallerPrincipal.CanActFor` (platform-admin OR tenant
match) and `Audit()` → `IAuditLog`. Reads/deletes derive tenant from the **stored record**,
not the route, so a guessed id cannot cross tenants.

### Worker plane (Tracer W)

| Surface | Entry | Stops at | Invariants |
|---|---|---|---|
| `worker:room-tick` | `RoomTickService.RunAsync` | `IGameSimulation.Apply` | room-ownership, backpressure, observability, **simulation-purity** |
| `worker:room-lease-renewal` | `RoomLeaseRenewalService.RunAsync` | `IRoomDirectory.TryRenew` | tenant, atomicity |
| `worker:telemetry-flush` | `TelemetryFlushService.RunAsync` | `AggregatingTelemetrySink.Snapshot` | observability, backpressure |

All run under `WorkerSupervisor` (independent restart + backoff, `WorkerRestartCount`, bounded
3 s drain) via the `ISupervisedWorker` seam. **Purity boundary:** `PeriodicTimer` /
`Stopwatch` / `Process` live here in `GameServer.Host` — never in the simulation kernel, which
advances on `ISimulationClock` (tick count). Lease renewal uses **renew, not claim**, to avoid
resurrecting lost ownership (anti-split-brain).

### Cross-process & cluster plane (Tracer X)

| Surface | Entry | Stops at | Invariants |
|---|---|---|---|
| `ipc:room-worker` (stdio JSON-RPC) | `RoomHost.Run` | `IGameSimulation.Apply` | crash-containment, tenant, protocol |
| `cluster:redis-directory` | `RedisRoomDirectory.TryClaim` | Redis `EVAL` | tenant, atomicity |
| `cluster:redis-placement` | `RedisRoomPlacement.PlaceScript` | Redis `EVAL` | tenant, atomicity, backpressure |

Redis claim is one atomic Lua `EVAL` (`GET` owner → claim only if unowned/ours → `SET PX` +
`ZADD`), giving mutual exclusion (no split-brain) and renewal in a single round-trip; every key
embeds the **escaped tenant** (`tenant/room`, injectivity-guarded). IPC contracts are typed
records; child crash → `worker_process_crashed` + restart, with `Kill(entireProcessTree)` on
cancel so no orphan.

## Architectural invariants → analyzer rules

| Invariant (from manifest) | Rule | Tier |
|---|---|---|
| Simulation purity — no wall-clock / ambient random in `GameServer.Simulation` | CITADEL0004 | 1 |
| Protocol payloads are immutable (`IMessagePayload` ⇒ `sealed record`) | CITADEL0005 | 1 |
| Bounded queues only (Transport/Routing collection fields) | CITADEL0006 | 1 |
| Room identity carries tenant context (`RoomId` param ⇒ `TenantContext`/`RoomKey`) | CITADEL0007 | 1 |
| `MessageType` dispatch is exhaustive | CITADEL0008 | 1 |
| Room state mutated only in `GameRoom.Tick` | CITADEL0009 | 2 |
| Telemetry calls carry required context tags | CITADEL0010 | 2 |
| Tenant-key provenance is a trusted source, not an inbound field | CITADEL0011 | 3 |
| Inbound `PlayerId` validated against the authenticated session | CITADEL0012 | 3 |
| `inbound.TraceId` propagated into handler telemetry | CITADEL0013 | 3 |

## Cross-cutting gaps surfaced by the trace

These are observations for follow-up, **not** blockers for the analyzer work:

1. **Untagged hot-path telemetry** (R, W) — `CommandsAccepted`, `ClientAcks`,
   `BackpressureRejections`, `TickDurationMs`, `MissedTicks` emit without correlation tags;
   `BackpressureRejections` folds three causes into one counter (wants a `reason` tag).
2. **IPC has no in-band tenant tag** (X) — `RoomRpcRequest/Response` carry no `TenantId`/`RoomKey`;
   cross-tenant isolation across the boundary rests on parent-side wiring discipline alone.
3. **Stringly-typed room label** (C, W) — `RoomScopedMetrics` keys on `"{tenant}/{room}"` split
   on `/`; fails closed but is fragile vs a typed `RoomKey`.
4. **Admin audit wildcard** (C) — `GET /api/v1/admin/rooms` audits `target="*"` regardless of the
   tenant filter applied.
5. **Cluster telemetry gap** (X) — Redis directory/placement and child-side RPC emit no metrics.
