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
| GET | `/api/v1/admin/rooms/{tenant}/{room}/replay/{toTick}` | **read-only** reconstruction of the room as of a past tick (sandbox; never touches the live room) |
| POST | `/api/v1/admin/rooms/{tenant}/{room}/rewind/{toTick}?reason=…` | **rewind** the live room to a past tick (game-admin of the tenant, or platform-admin) |
| POST | `/api/v1/admin/tenants/{tenant}/rewind?byTicks=N\|toTick=N&reason=…` | rewind **every room of one tenant** (game-admin/platform) |
| POST | `/api/v1/admin/rewind?byTicks=N\|toTick=N&reason=…` | rewind **every room, all tenants** (platform-admin only) |

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

## Replay & rewind (time travel)

The server checkpoints each room's authoritative state on a cadence and keeps an append-only
event log, so a room can be **deterministically reconstructed as of any past tick** within the
retained *rewind horizon*. Determinism is exact — the snapshot captures the random source's full
draw position (not just its seed), so even a stochastic game replays identically from a mid-history
checkpoint.

- **Replay** (`GET …/replay/{toTick}`) rebuilds the room in a sandbox and returns its state at that
  tick. Read-only: the live room is untouched. Use it to answer "what did this room look like then?".
- **Rewind** (`POST …/rewind/…`) rolls the *live* room back to a past tick and resumes it on a forked
  timeline — events after the target are discarded, and connected clients get a corrective keyframe.
  Bulk variants rewind every room of a tenant, or every room in the fleet (platform-admin only).
  `byTicks=N` rolls each room back by N from its own tick; `toTick=N` is an absolute target.

> The rewind horizon is `Realtime:EventLogRetentionTicks` (checkpoint cadence:
> `Realtime:CheckpointEveryTicks`). A target older than the horizon returns `409 BeyondHorizon`.
> Rewind requires a history-capable snapshot store: it is enabled on the in-memory host today;
> the durable Postgres path is latest-only until its history schema lands, where rewind returns
> `409 RewindUnavailable` rather than acting on partial data.

## Implementation

- `RealtimeServer.TryObserveRoom` — read-only observation, projects under the room lock.
- `RoomReplayService.ReplayTo` / `ReplaySession` — the deterministic replay engine (restore floor
  checkpoint + replay events to the target tick); `RealtimeServer.RewindRoom` swaps the rebuilt room
  in under the room lock and forks the timeline; `RoomRewindCoordinator` holds the `TickGate` so a
  bulk rewind never races the tick driver.
- `DeltaCompressor` is now lock-guarded, so an admin observer can read per-viewer lag
  concurrently with the tick/ack threads safely.
- The browser console reads the SSE stream with `fetch()` + a streaming body reader (not
  `EventSource`) so it can send the `Authorization` header.

Notes: the admin APIs are available in all environments (they are properly authenticated),
unlike the dev-only `/ws` and `/sim/telemetry` endpoints.
