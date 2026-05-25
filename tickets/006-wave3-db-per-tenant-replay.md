# 006 — Wave 3: DB-per-tenant persistence + replay

**Status:** open
**Area:** Persistence (green-field) / Routing recovery / Host
**Wave:** 3 (roadmap Phase 4 + green-field DB)
**Depends on:** Wave 0 (mostly independent of 2; needs the snapshot-header change coordinated with the realtime owner)
**Isolation:** new project built in a worktree; `Program.cs`/`RoomModel.cs`/`repo-analyzers.json`/`Citadel.slnx` touches serialized.

## Context
The deployed host is all in-memory: a restart loses every room/session/audit, and the CLAUDE.md
"database-per-tenant" claim is unimplemented (no EF/Npgsql/migrations). `RoomRecoveryService` works
but has nothing durable to recover from, and the RNG seed is not captured for cross-process replay.
See gap analysis areas 9, 13 (Critical).

## Scope
- Capture **seed + game-schema version** in `RoomSnapshot` header (`RoomModel.cs`, `GameRoom.cs`) so
  replay is deterministic across processes.
- New project **`GameServer.Persistence.Postgres`** (EF Core/Npgsql + migrations) implementing
  `ISnapshotStore`/`IEventLog`; durable records for tenant / game / **game-version** / room /
  session / audit, with **per-tenant database mapping**. Rank it in `repo-analyzers.json`
  (rank 1 — depends only on `GameServer.Abstractions`) and add to `Citadel.slnx`.
- Durable **host config** wiring the Postgres stores; admin read-only **replay** endpoint.
- Tests use **Testcontainers** (ephemeral Postgres) or a SQLite/in-memory provider, **category-gated**
  so the fast loop stays hermetic (no DB/sockets in `tests/GameServer.Tests`).

## Tests required
- #4 `RestartRecovery` (integration/replay): room state restored after a process/store restart.
- #6 `DeterministicReplayWithSeed` (replay): fresh process, snapshot+events ⇒ identical entity state.
- `PerTenantDbIsolation`: tenant A's writes never visible via tenant B's connection.
- `MigrationIdempotency`: migrations apply cleanly twice.

## Adversarial focus
Cross-tenant data leakage via shared connection/string; non-deterministic replay (uncaptured seed or
wall-clock creeping in); partial-write/crash safety (torn snapshot, half-applied event batch).

## Acceptance criteria
Standard DoD + named tests green (infra tests gated) + a host restart recovers rooms deterministically
+ per-tenant DB isolation proven + new project ranked and in the solution.

## References
- Plan/spec: Appendix A §9, §13; Test Gap Plan #4,#6
- `RoomRecoveryService.cs`, `RoomModel.cs`, `Persistence/FileSnapshotStore.cs` (pattern), `Program.cs`
