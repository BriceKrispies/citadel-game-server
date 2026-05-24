# Citadel Load Testing

`GameServer.LoadHarness` is a first-class distributed load generator. It simulates
many virtual game clients that speak the **real binary protobuf WebSocket protocol**
(`/realtime/v1/connect`) — the same contract a browser client uses. It is not a
browser/Selenium test and uses no JSON gameplay protocol.

## What the harness tests

- Connection capacity and ramp behavior (connect/handshake/join latency under load).
- Sustained and bursty input throughput over binary frames with monotonic sequencing.
- Room fan-out cost (many clients in one room → snapshot fan-out amplification).
- Resilience to malformed frames (server returns a typed `ServerError`, stays up).
- Receiver back-pressure isolation (slow clients must not stall others).
- Reconnect churn.
- It records: attempted/successful/failed/active connections; messages and bytes sent/received;
  server errors; unexpected closes; connect/handshake/join and input→snapshot latency;
  snapshot lag ticks; correction count; reconnect count; malformed-frame rejection count.
- Per-room verification (both sides): client-side per-room receive tallies (how many of a room's
  clients actually received snapshots, and the worst lag), plus a server-side cross-check that
  polls the admin API so every room's authoritative `subscriberCount` and advancing `tick` are
  proven from the server, not just inferred from the clients.

## Load is driven by ClientCommand

The harness sends `ClientCommand` (the message the platform forwards to the room and folds into
the authoritative simulation), NOT `ClientInputFrame`. Input frames are currently discarded by the
server's envelope mapper and do not count as connection liveness — a client that sends only input
frames is reaped at the idle deadline. The command string is configurable (`command`, default
`"MoveRight"` for `demo-game`; grid-walk uses `"Up"/"Down"/"Left"/"Right"`).

## Authentication

Control-plane calls (create-room, mint-token, admin observe) require an API key, sent as
`Authorization: Bearer <key>`. Set `apiKey` in the scenario; it must be authorized for the
scenario's `tenantId` (a per-tenant key suffices). Dev keys: `dev-tenant-a-key`, `dev-tenant-b-key`,
`dev-admin-key`. Static-token scenarios skip the control plane and need no key.

## Rooms and capacity (single box)

`roomCount` rooms are provisioned up front and clients are round-robin assigned across the real
room ids the control plane returns; each client's join token is minted for the exact room it joins
(the realtime server rejects a join whose room differs from the token's claim). With
`totalClients` divisible by `roomCount`, every room gets `totalClients / roomCount` players.

Fan-out is the dominant cost: N rooms × P players/room × snapshot rate sends. At 50 rooms × 200
players (`rooms-200.json`, 10k clients), a single dev box co-hosting the server and the harness
saturates around ~3–4k concurrent connections — clients then starve on their send loop, go silent,
and are idle-reaped (visible as high `unexpected closes` and `subscribers < expected` in the server
check). All 10k still connect cumulatively and every room receives data, but the box cannot hold
10k *concurrent* at that fan-out. To actually sustain 10k: run the harness on separate machine(s)
from the server (see scaling) and/or reduce per-room fan-out. `rooms-200-local.json` (10 rooms ×
200 = 2k) is sized to pass cleanly on one box.

## What it does NOT prove

- It does not prove correctness of game simulation — only protocol/transport behavior under load.
- A single local instance is bounded by one machine's CPU/socket limits; large numbers
  require many harness instances (see scaling).
- Latency includes client-side scheduling overhead; it is not a clean server-only measurement.
- It does not exercise real persistence, multi-node routing, or production auth.
- Default scenarios are small (≈100 clients) for local runs.

## Run the local smoke scenario

In one terminal, start the server:

```
dotnet run --project src/GameServer.Host
```

In another terminal, run the smoke scenario (100 clients, 10 rooms, 2 inputs/s, 60s):

```
dotnet run --project src/GameServer.LoadHarness -- load/scenarios/local-smoke.json
```

Results are written to `load/results/local-smoke/` (`*.result.json` + `*.timeseries.csv` +
`*.per-room.csv`), with a live console progress summary that ends in a per-room line and a
`server check: PASS/FAIL` line. Other scenarios live in `load/scenarios/`.

Per-room scenarios (200 players/room):

```
dotnet run --project src/GameServer.LoadHarness -- load/scenarios/rooms-200-local.json   # 10 rooms × 200 = 2k (passes on one box)
dotnet run --project src/GameServer.LoadHarness -- load/scenarios/rooms-200.json          # 50 rooms × 200 = 10k (needs distribution)
```

## Scale across many load-generator machines

The schema is per-instance: each harness instance runs its own slice of clients with a
unique `name`. To reach N total clients across M machines, run M instances each with
`totalClients = N / M` and a distinct `name` (e.g. `gen-01`, `gen-02`, …) so player ids
and result files do not collide. All instances point at the same `serverUrl`. Tokens are
obtained per instance from the control plane (`joinTokenMode: ControlPlane`). Aggregate
the per-instance JSON/CSV results offline.

Recommended ramp sequence (total clients across all generators):

```
100  →  1,000  →  5,000  →  10,000  →  25,000  →  50,000
```

Increase only after each step meets the pass criteria. Rough sizing: ~1k–5k clients per
generator machine depending on input rate; reach 50k with ~10–25 generators.

## Required server metrics to watch

Watch on `GameServer.Host` while a run is in flight (telemetry / `connections_*`,
`messages_*`, `tick_duration_ms`, `missed_ticks`, `backpressure_rejections`,
`snapshot_size_bytes`, `correction_rate`):

- active_connections vs. attempted (connection acceptance rate)
- tick_duration_ms and missed_ticks (is the authoritative loop keeping cadence?)
- messages_in/out per second and bytes/sec (fan-out amplification)
- invalid_messages / server errors (should track only the injected malformed %)
- backpressure_rejections (graceful shedding, not crashes)
- process CPU, memory, GC pause, and socket/file-descriptor counts

## Pass / fail criteria

A step **passes** when, during steady state:

- successful connections ≥ 99% of attempted; unexpected closes ≈ 0 (excluding injected churn).
- input→snapshot p95 latency stays within the target budget (e.g. ≤ 100 ms locally) and does not grow over time.
- tick_duration_ms p95 stays under the tick budget; missed_ticks ≈ 0.
- server errors equal only the injected malformed percentage; no `InternalServerError`.
- memory is stable (no unbounded growth over an idle-soak); no crashes.

A step **fails** if connections are refused below target, latency or snapshot lag grows
unbounded, missed_ticks climbs, memory leaks, or the server returns unexpected errors/closes.
