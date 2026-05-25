# 008 — Wave 5: matchmaking

**Status:** open
**Area:** Matchmaking (green-field, separate from room runtime)
**Wave:** 5 (roadmap green-field / Open Match-style)
**Depends on:** Wave 4 (allocation), Wave 7 control-plane integration points (join-token mint exists today)
**Isolation:** entirely new project in a worktree; integration touches (room-create/join-token, allocation) serialized.

## Context
There is no matchmaking of any kind today; rooms are entered via pre-minted join tokens. See gap
analysis area 4 (Missing). Open Match-style separation: matchmaking must stay out of the room runtime.

## Scope
- New project **`GameServer.Matchmaking`** (rank 2 — register in `repo-analyzers.json`, add to
  `Citadel.slnx`):
  - **Match tickets** (player intent + attributes), **pools**, **match functions/rules**,
    **evaluator**, **director/assignment** step.
  - Integrate assignment with the **allocation API** (Wave 4) + room creation + **join-token mint**
    so a matched player receives a token for the allocated room.
  - **Tenant/game/version-aware**: tickets and pools are scoped; no cross-tenant or cross-version
    matching.

## Tests required
- `TicketToAssignment`: a ticket flows pool → match function → evaluator → assignment → join token.
- `PoolRules`: match functions honor pool predicates.
- `MatchmakingTenantIsolation`: tickets never match across tenants/games/versions.

## Adversarial focus
Cross-tenant / cross-version match bleed; starvation/fairness (a tenant or skill bucket never
starved); assignment races (two directors assigning the same slot); ticket leakage across tenants.

## Acceptance criteria
Standard DoD + named tests green + matchmaking has zero dependency on the room runtime internals +
new project ranked and in the solution.

## References
- Plan/spec: Appendix A §4 (and the roadmap "Wave 5" entry)
- Wave 4 allocation API; control-plane join-token mint (`Program.cs` `/rooms/{id}/join-token`, `JoinTokenService`)
