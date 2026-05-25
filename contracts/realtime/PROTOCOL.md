# Citadel Realtime Protocol (v1)

The realtime protocol is the **primary gameplay transport**. It is binary, runs over
WebSocket, and is defined canonically by
`contracts/realtime/proto/gameserver.realtime.v1.proto`. This document states the
rules clients and servers must follow.

## Transport & framing

- Endpoint: `GET /realtime/v1/connect?joinToken=<token>` (WebSocket upgrade).
- **Binary frames only.** Every frame is a protobuf-encoded `RealtimeEnvelope`.
- **Text frames are rejected**: the server replies with a `ServerError`
  (`TEXT_FRAME_NOT_ALLOWED`) and closes with `DISCONNECT_REASON_TEXT_FRAME`.
- JSON exists only as a development/debug codec on a separate dev-only path; it is
  not part of the contract.

## Handshake

1. Client obtains a join token from the control plane
   (`POST /api/v1/rooms/{roomId}/join-token`).
2. Client opens the WebSocket with `?joinToken=<token>`. A missing/invalid token is
   rejected before the upgrade (`401`, `Unauthorized` / `InvalidJoinToken`).
3. Client sends `ClientHello` (carrying `requested_protocol_version`, `join_token`,
   `client_name`, and `supported_protocol_versions`).
4. Server replies `ServerWelcome` with the accepted protocol version, session id,
   and current server tick.

## Join-token usage

The join token authorizes a connection for a specific tenant/game/room/player. It is
issued by the control plane and validated at connect time. Tokens are opaque to the
client. (Token expiry/rotation is a planned addition.)

## Version negotiation

- The envelope and `ClientHello` carry `protocol_version`.
- The server accepts only versions it supports (currently `PROTOCOL_VERSION_V1`).
- An unsupported version yields a `ServerError` (`UNSUPPORTED_PROTOCOL_VERSION`) and a
  close with `DISCONNECT_REASON_UNSUPPORTED_VERSION`.

## Sequencing & acknowledgement

- Client messages carry a monotonically increasing `sequence` per connection.
- The server rejects stale/duplicate sequences (`SEQUENCE_REJECTED`).
- `ClientAck` carries `acked_sequence` / `acked_server_tick` so the server can
  track what the client has confirmed.
- `client_tick` and `server_tick` carry the two clocks for prediction/reconciliation.

## Server authority

The server is authoritative. Client messages are **intent only** — input frames,
commands, acks, pings. The server never trusts client-reported state. Authoritative
state is conveyed by `ServerSnapshot`, `ServerDelta`, and `ServerCorrection`.

## Client prediction & server reconciliation

- Clients may locally predict using `ClientInputFrame` (carrying `client_tick`).
- The server simulates authoritatively and emits `ServerSnapshot`/`ServerDelta`.
- When a client's predicted state diverges, the server emits a `ServerCorrection`
  carrying the authoritative entity and the `acked_client_tick` it reflects; the
  client must reconcile to it.

### `ClientInputFrame` is accepted but not yet simulated (intentional, v1)

The server currently **accepts `ClientInputFrame` but does not fold it into the
authoritative simulation** — it is acknowledged at the edge and dropped, not enqueued as
a command. The authoritative command path is `ClientCommand` (a validated, sequenced
intent the room's game applies on tick). **Load and gameplay must drive `ClientCommand`,
not `ClientInputFrame`** — an input frame does not advance state and does not count as a
command, so a client sending only input frames will appear idle.

This is a documented, deliberate limitation, not a silent drop: predicted-input batching
(folding `ClientInputFrame.commands` into the room queue with client-tick reconciliation)
is a planned addition. Until then the contract is explicit so clients are not surprised.
An input frame still proves connection liveness at the transport (a received frame), so it
does not trip the idle reaper.

## Logical channel multiplexing

A single physical WebSocket multiplexes logical channels via `logical_channel`:
`GAMEPLAY`, `PRESENCE`, `CHAT`, `SPECTATOR`, `ADMIN`, `TELEMETRY`. Gameplay is the
default; other channels may be throttled or shed independently under load.

## Snapshot vs delta

- `ServerSnapshot` is a full authoritative **keyframe**: the complete relevant entity
  set for a tick. The client **replaces** its view with it.
- `ServerDelta` is **incremental**: `changed_entities` (fields the client merges) and
  `removed_entities` (entity ids the client deletes) between `from_server_tick` and
  `to_server_tick`. The client **applies it on top of the baseline it acked at**
  `from_server_tick`.
- Selection (server-side): a viewer with no acknowledged baseline (just joined, or
  reset on reconnect) gets a full `ServerSnapshot` keyframe; once it has acked a tick,
  subsequent ticks are sent as `ServerDelta`. The delta baseline advances **only on a
  `ClientAck`**, never because a write to the socket succeeded — so a dropped frame
  self-heals (the server keeps resending until the client confirms).
- A client that receives a `ServerDelta` whose `from_server_tick` does not match its
  last applied tick is missing the baseline: it should NOT ack, which makes the server
  resend until it catches up (or, on reconnect, a fresh keyframe is sent).
- The opaque entity model supersedes the legacy typed `PlayerState` views on
  `ServerSnapshot.players` / `ServerDelta.changed` / `ServerCorrection.authoritative`
  (fields kept reserved for back-compat, no longer populated). Game state travels as
  opaque `EntityState.payload` bytes the platform never interprets.

## Room lifecycle

- **Join** (`ClientJoinRoom`): the platform validates identity/capacity, then the
  room's game decides admission via its `CanJoin` rule. A game refusal yields a typed
  `ServerError` (`PLAYER_NOT_IN_ROOM` on the wire) and grants no membership. On success
  the server emits a `ServerEvent` (`player_joined`).
- **Leave** (`ClientLeaveRoom`): frees the player's room membership **without dropping
  the connection** (the client may rejoin), fires the game's `OnLeave`, and emits a
  `ServerEvent` (`player_left`). A disconnect also fires `OnLeave`.
- **Terminate**: when a room's last member leaves (under the host's reap lifecycle) the
  room is torn down and the game's `OnTerminate` fires once. Lifecycle hooks have no-op
  defaults, so a game that ignores them behaves identically (Liskov).

## listRooms (deferred)

There is **no client-facing room-discovery message** in v1, by design. Room placement is
authoritative and tenant-scoped: a client connects with a join token already minted by
the control plane for a specific `(tenant, game, room, player)`, so a client never needs
to enumerate rooms. Room listing/discovery is an **operator/admin concern** served by the
control-plane HTTP API (and the in-process `TryObserveRoom` admin view), not the realtime
data plane. Adding a realtime `listRooms` would put a control-plane query on the hot path
and risk leaking other tenants' rooms; it is deferred unless a concrete spectator/lobby
use case requires it, at which point it ships as an additive message on the `ADMIN` or
`SPECTATOR` channel.

## Reconnect / resume

Reconnect is supported as a **fresh keyframe**, not stateful session resume. A
reconnecting client re-runs hello → join under the same identity; the server resets that
viewer's delta baseline (`Resubscribe`), so the next tick re-establishes a full
`ServerSnapshot` keyframe and the client rebuilds its view from scratch. This is the
correct, robust behavior: a reconnected client cannot be assumed to still hold any state a
previous connection acknowledged. **Stateful resume** (replaying the exact unacked frame
window across a new connection to avoid a full keyframe) is **deferred** — it is a
bandwidth optimization, not a correctness requirement, and the keyframe path already
reconstructs identical state (proven by the reconnect reconstruction tests).

## Ping / pong

- `ClientPing` carries `client_tick` and a `nonce`.
- The server replies `ServerPong` echoing the `nonce` plus `server_tick` for RTT and
  clock-offset estimation.

## Disconnect behavior

The server closes with a stable `DisconnectReason`
(`PROTOCOL_VIOLATION`, `UNSUPPORTED_VERSION`, `TEXT_FRAME`, `UNAUTHORIZED`,
`RATE_LIMITED`, `SERVER_SHUTDOWN`, …). Fatal `ServerError`s set `fatal = true` and a
`disconnect_reason`, and are followed by a close.

## Error handling

Malformed binary frames yield a `ServerError` (`MALFORMED_FRAME`); the connection
survives a single malformed frame. Unknown game payloads fail explicitly
(`UNKNOWN_GAME_MESSAGE`). See `contracts/realtime/ERRORS.md`.

## Tenant isolation

`tenant_id` is carried on every envelope and resolved authoritatively at connect.
A connection can never observe another tenant's rooms, players, snapshots, or events.
Rooms are keyed by `(tenantId, roomId)`.

## Game-specific extension

Game payloads the platform does not natively model use `GameMessage`
(`game_message_type`, `game_payload` bytes, `game_schema_version`). The platform
never interprets `game_payload`; an unregistered type/version fails explicitly with
`UNKNOWN_GAME_MESSAGE`.

## Compatibility

See `contracts/realtime/COMPATIBILITY.md`. In short: field numbers are never reused,
additive changes are preferred, and breaking changes require a new major version.
