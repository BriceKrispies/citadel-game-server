# 009 — Wave 6: load/failure conformance + degradation

**Status:** open
**Area:** Transport (degradation) / LoadHarness / Host
**Wave:** 6 (roadmap Phase 6)
**Depends on:** Waves 1–4 (needs the wired safety controls + cluster for node-death)
**Hot-file owner:** realtime-core owner for the `DegradationController` wiring; load owner for scenarios.

## Context
`DegradationController` is built but unreferenced — the server has no graceful degradation ladder and
fails at a cliff. Load coverage is single-tenant only; missing large-spectator, node-death,
admin-under-load, and multi-tenant scenarios. See gap analysis areas 10, 11.

## Scope
- Wire `DegradationController` (`src/GameServer.Transport/DegradationController.cs`) into the tick
  driver/edge so it **sheds optional load under sustained overload** along a **monotonic ladder**
  (e.g. reduce spectator snapshot rate → drop non-critical telemetry → reject new rooms → reject new
  connections) driven by `missed_ticks`/`tick_duration_ms`.
- Add load scenarios (`load/scenarios/*.json`): **large-spectator**, **node-death-during-match**,
  **admin-under-load**, **multi-tenant**.
- Prove **node-death survivability** (with Wave 4 cluster): a killed node's rooms recover/migrate.

## Tests required
- `DegradationLadderUnderLoad` (load/integration): overload escalates the ladder, never regressing
  below authoritative correctness; recovers when load subsides.
- `NodeDeathSurvivable` (integration): kill a node mid-match; clients recover.
- The new scenarios run green in the harness with documented pass criteria.

## Adversarial focus
Combined overload (command flood + slow consumers + many rooms simultaneously) must preserve other
tenants' SLOs; the ladder must be monotonic and must never drop authoritative simulation; admin
queries under load must not starve the hot path.

## Acceptance criteria
Standard DoD + named tests green + degradation ladder fires under overload instead of a cliff +
node death is survivable + multi-tenant load proves isolation under stress.

## References
- Plan/spec: Appendix A §10, §11 (and roadmap "Wave 6")
- `Transport/DegradationController.cs`, `RealtimeServer.cs`/tick driver, `LoadHarness/*`, `load/scenarios/*`
