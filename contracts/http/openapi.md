# Citadel Control-Plane HTTP API (v1)

The HTTP API is **control-plane only**: health, version, game catalog, room and
session lifecycle, and join-token issuance. **Gameplay never uses REST** — it flows
over the realtime WebSocket endpoint (see `contracts/realtime/PROTOCOL.md`).

- Base URL (local): `http://localhost:5000`
- Content type: `application/json`
- All errors return a typed body: `{ "code": "<ErrorCode>", "message": "<human text>" }`
  where `code` is drawn from the shared error vocabulary (`contracts/realtime/ERRORS.md`).

## Operational

| Method | Path | Response | Notes |
| --- | --- | --- | --- |
| GET | `/health` | `200 { "status": "healthy" }` | Liveness. |
| GET | `/ready` | `200 { "status": "ready" }` | Readiness. |
| GET | `/version` | `200 { "protocolVersion": 1, "realtimeProtocol": "gameserver.realtime.v1" }` | Build/protocol info. |

## Game catalog

| Method | Path | Success | Errors |
| --- | --- | --- | --- |
| GET | `/api/v1/games` | `200 GameSummary[]` | — |
| GET | `/api/v1/games/{gameId}` | `200 GameDetail` | `404 GameNotFound` |

```jsonc
// GameSummary
{ "gameId": "demo-game", "name": "Demo Game" }
// GameDetail
{ "gameId": "demo-game", "name": "Demo Game", "description": "...", "protocolVersion": 1 }
```

## Rooms

| Method | Path | Body | Success | Errors |
| --- | --- | --- | --- | --- |
| POST | `/api/v1/rooms` | `CreateRoomRequest` | `201 RoomContract` | `404 GameNotFound` |
| GET | `/api/v1/rooms/{roomId}` | — | `200 RoomContract` | `404 RoomNotFound` |
| POST | `/api/v1/rooms/{roomId}/join-token` | `CreateJoinTokenRequest` | `201 JoinTokenContract` | `404 RoomNotFound` |

```jsonc
// CreateRoomRequest
{ "tenantId": "tenant-a", "gameId": "demo-game" }
// RoomContract
{ "roomId": "room-1", "tenantId": "tenant-a", "gameId": "demo-game", "status": "open" }
// CreateJoinTokenRequest
{ "playerId": "player-1" }
// JoinTokenContract
{ "token": "jt-1", "tenantId": "tenant-a", "gameId": "demo-game", "roomId": "room-1", "playerId": "player-1" }
```

Creating a room records **control-plane metadata only**; it does not create or run
an authoritative simulation room. The simulation room is created lazily in the
realtime data plane when a player joins.

## Sessions

| Method | Path | Body | Success | Errors |
| --- | --- | --- | --- | --- |
| POST | `/api/v1/sessions` | `CreateSessionRequest` | `201 SessionContract` | — |
| DELETE | `/api/v1/sessions/{sessionId}` | — | `204 No Content` | `404 SessionNotFound` |

```jsonc
// CreateSessionRequest
{ "tenantId": "tenant-a", "playerId": "player-1" }
// SessionContract
{ "sessionId": "control-session-1", "tenantId": "tenant-a", "playerId": "player-1", "status": "active" }
```

## Realtime handoff

To play, a client:
1. `POST /api/v1/rooms` (or discovers a room),
2. `POST /api/v1/rooms/{roomId}/join-token` to obtain a `JoinTokenContract`,
3. opens a WebSocket to `GET /realtime/v1/connect?joinToken=<token>` and speaks the
   **binary protobuf** realtime protocol.
