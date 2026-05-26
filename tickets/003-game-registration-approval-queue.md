# 003 — Game-registration approval queue (status + stored contract)

**Status:** open
**Area:** ControlPlane / Persistence / Admin / Host
**Depends on:** 001 (validator), 002 (intent payload)

## Context
Games are created immediately-active today (`POST /api/v1/games`, no status, no stored schema). The vision
needs: a game submits its contract -> sits in a PENDING queue -> the server owner (platform-admin) reviews
the schema and accepts/rejects -> only an accepted game can interact. The validator is compiled + cached on
acceptance (off the hot path).

## Scope
- `IGameRegistry`/`GameDetail`: add `GameStatus {Pending,Accepted,Rejected}` + a stored `CommandContract?`.
  Default `Accepted` for existing/seeded games (back-compat). InMemory + Postgres impls.
- Postgres: backward-compatible migration adding `status` (default 'accepted') + `command_schema` (nullable).
- `POST /api/v1/games`: when a contract is supplied -> create `Pending`; else `Accepted` (back-compat).
- Admin queue (platform-admin, audited): `GET /api/v1/admin/games/pending`,
  `POST /api/v1/admin/games/{id}/approve`, `POST /api/v1/admin/games/{id}/reject`. Approve compiles the
  validator (`ISchemaCompiler`) and registers it; reject records a reason.
- Realtime join gate: refuse a join whose game is not `Accepted` — at `RealtimeServer.HandleJoinAsync`
  (before room create) AND at the join-token mint (fail closed). Cache the validator on the connection here.

## Tests / acceptance
- `ControlPlaneGameApprovalAuthz` (every endpoint authn + platform-admin + audited, incl. denial path).
- `UnacceptedGame_CannotJoin` (realtime gate rejects with a typed ServerError; no orphan room).
- `ApprovedGame_CompilesAndCachesValidator`; durable round-trip (status+contract persist + reload).
- A game/version can be provisioned and approved through audited APIs; no code change to add a game.

## References
- Approved plan: `~/.claude/plans/i-want-a-full-piped-candle.md` (Phase 1).
- `src/GameServer.ControlPlane/InMemoryGameCatalog.cs`, `src/GameServer.Persistence.Postgres/{PostgresGameRegistry,DurableRecords}.cs` + Migrations, `src/GameServer.Host/Program.cs` (`/api/v1/games`, `/admin/*`), `RealtimeServer.HandleJoinAsync`.
