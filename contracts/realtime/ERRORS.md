# Realtime & Control-Plane Error Codes

A single, stable error vocabulary is shared by the realtime protocol (`ServerError.code`,
the protobuf `ErrorCode` enum) and the HTTP control plane (`ApiError.code` strings).
Codes are stable identifiers; clients should branch on the code, never the message.

| Code | Realtime enum | Typical surface | Meaning |
| --- | --- | --- | --- |
| `UnsupportedProtocolVersion` | `ERROR_CODE_UNSUPPORTED_PROTOCOL_VERSION` | realtime | Client requested a protocol version the server does not speak. |
| `MalformedFrame` | `ERROR_CODE_MALFORMED_FRAME` | realtime | A binary frame could not be decoded as a valid `RealtimeEnvelope`, or an unknown platform command was sent. |
| `TextFrameNotAllowed` | `ERROR_CODE_TEXT_FRAME_NOT_ALLOWED` | realtime | A WebSocket text frame was received; the protocol is binary-only. |
| `Unauthorized` | `ERROR_CODE_UNAUTHORIZED` | realtime / HTTP | Missing credentials/join token. |
| `InvalidJoinToken` | `ERROR_CODE_INVALID_JOIN_TOKEN` | realtime / HTTP | Join token is present but not valid. |
| `TenantNotFound` | `ERROR_CODE_TENANT_NOT_FOUND` | realtime / HTTP | The tenant could not be resolved. |
| `GameNotFound` | `ERROR_CODE_GAME_NOT_FOUND` | HTTP | The game id is not in the catalog. |
| `RoomNotFound` | `ERROR_CODE_ROOM_NOT_FOUND` | realtime / HTTP | The room does not exist / is unavailable. |
| `SessionNotFound` | `ERROR_CODE_SESSION_NOT_FOUND` | HTTP | The session id does not exist. |
| `PlayerNotInRoom` | `ERROR_CODE_PLAYER_NOT_IN_ROOM` | realtime | A command/input arrived for a player not joined to the room. |
| `SequenceRejected` | `ERROR_CODE_SEQUENCE_REJECTED` | realtime | Stale or duplicate sequence number. |
| `RateLimited` | `ERROR_CODE_RATE_LIMITED` | realtime / HTTP | The client exceeded its rate budget. |
| `BackpressureRejected` | `ERROR_CODE_BACKPRESSURE_REJECTED` | realtime | The server shed the message under overload. |
| `InternalServerError` | `ERROR_CODE_INTERNAL_SERVER_ERROR` | realtime / HTTP | Unexpected server failure. |
| `UnknownGameMessage` | `ERROR_CODE_UNKNOWN_GAME_MESSAGE` | realtime | A `GameMessage` referenced an unregistered game message type / schema version. Mandated explicit failure for unknown game payloads. |

## Realtime delivery

- Realtime errors are delivered as a `RealtimeEnvelope` whose payload is `ServerError`
  (`code`, `message`, `fatal`, `disconnect_reason`).
- A `fatal` error is followed by a connection close carrying a stable
  `DisconnectReason`.
- Non-fatal errors (e.g. a single `MalformedFrame`) keep the connection open.

## HTTP delivery

- HTTP errors return the matching status code with body `{ "code", "message" }`:
  `404` for `*NotFound`, `401` for `Unauthorized`/`InvalidJoinToken`,
  `429` for `RateLimited`, `400` for malformed requests, `500` for
  `InternalServerError`.
