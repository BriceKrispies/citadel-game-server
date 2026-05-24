# Citadel Simulation Console

A live, in-browser visual harness for watching many clients drive the authoritative
server at once. Every dot on the field is one **real** WebSocket client receiving the
server's authoritative snapshots; the side panel streams **aggregate server telemetry**
(what the server is actually broadcasting) in real time.

This is a development tool. It complements the two existing harnesses:

- `DEMO.md` — one manual browser client (click to move).
- `LOAD_TESTING.md` / `GameServer.LoadHarness` — thousands of headless clients over the
  real binary protobuf protocol, writing offline CSV/JSON results.

The console sits in between: tens of clients, **driven and rendered live in the browser**,
so you can see clients responding and telemetry reacting together.

## Run

```
dotnet run --project src/GameServer.Host
```

The host listens on `http://localhost:5000`. Open:

```
http://localhost:5000/sim.html
```

> The console uses the dev-only `/ws` (JSON) endpoint and a dev-only `/sim/telemetry`
> SSE feed, both mapped only in the **Development** environment. `Properties/launchSettings.json`
> sets `ASPNETCORE_ENVIRONMENT=Development`, so a plain `dotnet run` is enough. (Running
> with `ASPNETCORE_ENVIRONMENT=Production` disables these dev endpoints by design.)

## Using it

1. Set **Clients** (e.g. 24), **Room**, and **Moves/sec/client**.
2. Click **Spawn**. Each client opens a WebSocket, sends `ClientHello` → `ClientJoinRoom`
   (grid-walk), then streams `ClientCommand` (Up/Down/Left/Right) on a biased random walk.
3. Watch the **field**: each dot is a client at its authoritative grid position, decoded
   from the snapshots the server broadcasts back. All clients in a room see every player,
   so the swarm you see is the room-wide authoritative state.
4. Watch the **telemetry panel** (updates every second from the server):
   - `entities/s broadcast` — replicated payload volume. In grid-walk's full-snapshot
     mode this is ≈ clients × clients × ticks/s, so it climbs steeply with client count —
     a vivid view of fan-out amplification.
   - `messages out/s`, `snapshots/s`, `messages in/s`, `cmds accepted/s`.
   - `tick ms` mean/max — is the authoritative loop keeping its 100 ms cadence?
   - `backpressure/s` — commands shed because a room's queue was full (the bounded-queue
     overload path). Stays 0 under normal load.
   - `conns opened` / `conns dropped`.
   - Sparklines for entities/s and tick ms over the last 60 s.
5. **+ Add** ramps more clients into the current run; **Stop all** closes every socket.

Each **Spawn** uses a fresh room (`<room>-<run>`) so server-persisted walkers from a
previous run don't linger as motionless ghosts.

## How it works

- **Clients run in the browser** (one real WebSocket each) over the dev JSON codec, so
  what you see is the genuine connect → hello → join → command → snapshot path through
  the real `RealtimeServer`, not a mock. Practical ceiling is ~100–150 clients (browser
  socket limits); for larger runs use `GameServer.LoadHarness`.
- **Telemetry** is read-only. `GET /sim/telemetry` (SSE) observes the same
  `AggregatingTelemetrySink` the hot path feeds and emits one `TelemetryRates` frame per
  second, computed by `TelemetryRates.Between` — the *same* interval math the periodic
  telemetry log flush uses (one source of truth, no duplication).

## Validation

```
dotnet test tests/GameServer.Tests/GameServer.Tests.csproj      # incl. TelemetryRates tests
dotnet build Citadel.slnx
```

Smoke (manual): run the host, open `/sim.html`, Spawn ~24 clients, confirm the dots move
and `entities/s` climbs into the thousands while `tick ms` stays low and `backpressure/s`
stays 0.
