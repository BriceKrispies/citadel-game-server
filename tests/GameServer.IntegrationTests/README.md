# GameServer.IntegrationTests

Realistic, scale-focused integration tests that drive the **real in-process
`RealtimeServer`** (not mocks) and emit **evidence artifacts** demonstrating known
scaling gaps and their fixes. Each scenario writes `artifacts/<scenario>/report.md`
(human-readable) and `report.json` (machine-readable) at the repo root. `artifacts/` is
git-ignored — the reports are regenerated each run.

These are deliberately separate from the fast unit suite (`GameServer.Tests`): they do
real wall-clock/CPU work, so run them explicitly.

## Run

```
dotnet test tests/GameServer.IntegrationTests/GameServer.IntegrationTests.csproj
```

Then read the generated `artifacts/*/report.md`.

## Running the Redis scenario against a real engine (Podman)

`RedisRoomDirectoryScenario` is the only runnable proof of the real cross-node room
directory/placement (`GameServer.Cluster.Redis` + its Lua). It is a `[SkippableFact]`
using Testcontainers to spin a throwaway `redis:7-alpine`: with **no reachable container
engine it SKIPS green** (the suite stays honest), so it does nothing in a vanilla dev
checkout. To run it for real, point Testcontainers at **Podman** (no Docker Desktop):

```
pwsh scripts/integration-podman.ps1
```

The script is idempotent. It: starts the Podman machine if needed; resolves the
Docker-API-compatible **named pipe** from `podman machine inspect` and exports
`DOCKER_HOST=npipe://./pipe/<machine>` (no hardcoded user path); disables the Ryuk
reaper (`TESTCONTAINERS_RYUK_DISABLED=true`) because rootless Podman rejects its
privileges, and instead **owns teardown itself** (Testcontainers disposes each
container, and the script sweeps any leftover `redis:7-alpine` in `finally`); warms the
`docker.io/library/redis:7-alpine` pull to surface registry misconfig early; then runs
the suite. Verify no leaks afterward with `podman ps -a`.

If a clean Podman install can't pull `redis:7-alpine`, add docker.io to
`registries.conf`: `unqualified-search-registries = ["docker.io"]`.

## CI requirement

To run this scenario in CI (rather than letting it skip), the pipeline must expose a
container engine to Testcontainers: provision Podman (or Docker) on the runner, ensure
its Docker-API socket is up, and export `DOCKER_HOST` (+ `TESTCONTAINERS_RYUK_DISABLED`
under rootless Podman) before `dotnet test tests/GameServer.IntegrationTests` — exactly
what `scripts/integration-podman.ps1` does locally. Building the pipeline itself is out
of scope here; this is the standing requirement to wire it in. The fast unit loop
(`tests/GameServer.Tests`) is hermetic and must **never** gain a container/socket
dependency.

## Scenarios (the six gaps)

| # | scenario | gap | status | what the artifact shows |
|---|---|---|---|---|
| 1 | `01-parallel-ticking` | serial tick loop = head-of-line blocking | **fixed** | serial vs parallel cycle time + ASCII timelines; ~Nx speedup on N cores |
| 2 | `02-multi-node-ownership` | no room→node ownership (split brain) | demonstrate-only | same RoomKey on two nodes holds two different truths |
| 3 | `03-room-lifecycle` | empty rooms + dead subscribers leak | **fixed** | churn 50 rooms → Persist leaks 50, Reap leaves 0 |
| 4 | `04-admission-control` | unbounded connection/room acceptance | **fixed** | connections capped, per-tenant quota, max-rooms shed cleanly |
| 5 | `05-event-log-retention` | append-only event log grows forever | **fixed** | 500-tick room: unbounded keeps 500 events, bounded keeps 64 |
| 6 | `06-per-room-telemetry` | global telemetry can't name the hot room | **fixed** | global mean hides a 40ms room; per-room view names it |

"Fixed" gaps changed production behavior; the fix is the new default in the host
(`GameServer.Host/Program.cs`) and is exercised here as a before/after. The before
behavior is preserved as an explicit, named option (e.g. `RoomLifecycle.Persist`,
`AdmissionPolicy.Unlimited`, retention `0`) so the contrast is a real measurement, not a
claim.

Gap #2 (and durable cross-node persistence in #5) cannot be honestly *fixed* in a single
process; those scenarios document the failure mode and what is missing
(room→node ownership, sticky routing, ownership handoff).

## How it's built

- `IntegrationHarness` wires the real stack from production types only.
- `BusyGame` gives a room a configurable per-tick CPU cost (for scheduling/telemetry scenarios).
- `ArtifactWriter` resolves the repo root and writes the JSON + Markdown reports.

Production seams added/changed for these fixes: `IRoomTickScheduler`
(`Sequential`/`Parallel`), `RoomLifecycle`, `AdmissionPolicy`, `IEventLog.TruncateThrough`,
and `RoomScopedMetrics` — each with co-located unit tests in `GameServer.Tests`.
