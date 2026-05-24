# Citadel Admin Console — drop in to a live session

Read-only operator tooling to **observe a live room exactly as the server sees it** — the
authoritative entity state and each connected session's lag — for debugging "what is this
player seeing / why are they stuck", without attaching a debugger or mutating game state.

Authenticated (control-plane key), tenant-authorized, and audited. Strictly read-only: it
projects authoritative state and reads existing bookkeeping; it never enqueues commands or
changes a room.

## Run

```
dotnet run --project src/GameServer.Host
```

Open `http://localhost:5000/admin.html`, enter an admin key (dev default `dev-admin-key`),
and click **List rooms**, then click a room to drop in. Connect some clients first (e.g.
the simulation console at `/sim.html`, or the demo at `/`).

## Endpoints (`/api/v1/admin`, Bearer auth)

All require `Authorization: Bearer <control-plane key>`. A caller sees only rooms in
tenants it may act for; a platform-admin key sees all. Every call is audited.

| method | path | purpose |
|---|---|---|
| GET | `/api/v1/admin/rooms` | active rooms (tenant-scoped) + hottest rooms by tick cost |
| GET | `/api/v1/admin/rooms/{tenant}/{room}` | one observation (entities + viewers) |
| GET | `/api/v1/admin/rooms/{tenant}/{room}/observe` | live SSE stream (~4 Hz) of the observation |

Example:

```
curl -H "Authorization: Bearer dev-admin-key" \
  http://localhost:5000/api/v1/admin/rooms/tenant-a/arena
```

returns the authoritative tick, every entity (`entityId`, `version`, relevance key `x/y/group`,
and the opaque `payloadBase64`), and every session (`connectionId`, `playerId`,
`pendingSnapshots` = unacknowledged backlog / lag).

## What you can and cannot see

- **Can**: the authoritative state the server holds, plotted by each entity's relevance key
  (works for any game without decoding the opaque payload), plus per-session lag — so a
  stalled or non-acking client shows up as a climbing `pendingSnapshots`.
- **Cannot**: the literal pixels on the player's screen. The server is authoritative; if a
  client predicts/interpolates, its view can diverge, and the server only knows what it
  *sent* and what the client *acked*. "Exactly what they see" server-side means authoritative
  state at their last-acked tick.

> Per-session lag is only meaningful under delta replication (full-snapshot games keep no
> per-viewer baseline, so `pendingSnapshots` stays 0). The hottest-rooms list is the
> bounded per-room tick-cost telemetry (see `tests/GameServer.IntegrationTests`, gap #6).

## Implementation

- `RealtimeServer.TryObserveRoom` — read-only observation, projects under the room lock.
- `DeltaCompressor` is now lock-guarded, so an admin observer can read per-viewer lag
  concurrently with the tick/ack threads safely.
- The browser console reads the SSE stream with `fetch()` + a streaming body reader (not
  `EventSource`) so it can send the `Authorization` header.

Notes: the admin APIs are available in all environments (they are properly authenticated),
unlike the dev-only `/ws` and `/sim/telemetry` endpoints.
