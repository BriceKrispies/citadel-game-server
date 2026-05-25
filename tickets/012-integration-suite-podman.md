# 012 — Run the integration suite's containers on Podman (and actually exercise it)

**Status:** open
**Area:** tests / ops / CI
**Depends on:** nothing hard — `RedisRoomDirectoryScenario` already exists and skips cleanly when no
container engine is reachable. This ticket makes the engine available (Podman) so it runs green.

## Context
`tests/GameServer.IntegrationTests` gained a Testcontainers-backed Redis scenario,
`RedisRoomDirectoryScenario`, which is the ONLY runnable proof of the real cross-node room directory /
placement (`GameServer.Cluster.Redis` — `RedisRoomDirectory`, `RedisRoomPlacement`, and their Lua). It
is a `[SkippableFact]`: with no reachable container engine it throws `SkipException` and the suite stays
green — which is exactly what happens in dev today (no Docker installed). So the Redis adapter + Lua are
currently **unverified locally**.

The repo already standardizes on **Podman** (ticket 001 builds and runs the hardened host via
`podman build` / `podman run`; `scripts/podman-build.ps1`, `scripts/security-test.ps1`). We should drive
Testcontainers against Podman too — no Docker Desktop — and run the scenario for real.

## Scope
- **Point Testcontainers .NET at the Podman socket** (no code change to the test; it's host/env config):
  - Windows: `podman machine` exposes a Docker-API-compatible socket. Resolve its path via
    `podman machine inspect` and set `DOCKER_HOST` (npipe on Windows / unix socket on Linux), or write a
    generated `~/.testcontainers.properties` with `docker.host=...`. Do NOT hardcode a user-specific
    socket path in the repo.
  - **Ryuk** (Testcontainers' resource reaper): rootless Podman often needs
    `TESTCONTAINERS_RYUK_DISABLED=true` (then ensure the script tears containers down itself) OR a
    Podman socket exposed with privileges Ryuk accepts. Decide one, document the trade-off.
  - Registry: `Testcontainers.Redis` pulls `redis:7-alpine` — ensure Podman can pull from `docker.io`
    (document `unqualified-search-registries`/registry config so a clean Podman install works).
- **`scripts/integration-podman.ps1`** (mirroring ticket 001's scripts): ensure the Podman machine +
  API socket are up (`podman machine start`, enable the socket service), export the Testcontainers env
  (`DOCKER_HOST`, Ryuk flag), then `dotnet test tests/GameServer.IntegrationTests`. Idempotent; clear
  failure if Podman isn't installed.
- Keep the existing `SkipException` fallback in `RedisRoomDirectoryScenario` — the script's job is to
  make the engine reachable so the scenario RUNS rather than skips; with no engine at all it must still
  skip green.
- **Exercise it**: run the script, confirm `RedisRoomDirectoryScenario` reports **passed** (claim/fence/
  count/release cross-instance, capacity placement, tenant-key collision-freedom, lease renewal). Fix
  anything the real Redis run surfaces that the static review couldn't (Lua arg indexing, RedisValue
  conversions, `cur == ARGV[1]` type compares, `ZCOUNT`/`ZCARD` bounds, empty-string place return).
- Short doc note (README or `LOAD_TESTING.md`/a new `tests/GameServer.IntegrationTests/README` line):
  how to run the integration suite with Podman locally; capture the CI requirement (wire the Podman
  socket into the pipeline) even if building CI is out of scope.

## Acceptance criteria
- `pwsh scripts/integration-podman.ps1` brings up Podman, runs the suite, and `RedisRoomDirectoryScenario`
  is **passed (not skipped)** — proving the Redis directory/placement against a real Redis, no Docker Desktop.
- On a machine with no container engine at all, `dotnet test tests/GameServer.IntegrationTests` still
  passes (scenario skips with a clear reason).
- Containers are always cleaned up (Ryuk or explicit teardown), no leaked `redis:7-alpine` containers.
- How-to documented; CI requirement captured.

## References
- `tests/GameServer.IntegrationTests/RedisRoomDirectoryScenario.cs` (the `[SkippableFact]`, `Testcontainers.Redis`)
- `src/GameServer.Cluster.Redis/RedisRoomDirectory.cs`, `RedisRoomPlacement.cs` (the code under test + Lua)
- Podman scripting precedent: ticket 001 (`scripts/podman-build.ps1`, `scripts/security-test.ps1`)
