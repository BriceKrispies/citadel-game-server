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
  carrying the authoritative state and the `acked_client_tick` it reflects; the
  client must reconcile to it.

## Logical channel multiplexing

A single physical WebSocket multiplexes logical channels via `logical_channel`:
`GAMEPLAY`, `PRESENCE`, `CHAT`, `SPECTATOR`, `ADMIN`, `TELEMETRY`. Gameplay is the
default; other channels may be throttled or shed independently under load.

## Snapshot vs delta

- `ServerSnapshot` is a full authoritative state for a tick.
- `ServerDelta` carries only what changed between `from_server_tick` and
  `to_server_tick`. Clients must be able to apply both; deltas reference a prior
  snapshot/tick.

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
