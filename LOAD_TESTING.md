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

Results are written to `load/results/local-smoke/` (`*.result.json` + `*.timeseries.csv`),
with a live console progress summary. Other scenarios live in `load/scenarios/`.

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
