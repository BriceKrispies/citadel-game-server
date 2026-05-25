# 005 — Wave 2: room lifecycle, protocol, deltas, SDK

**Status:** open
**Area:** Protocol / Transport / Simulation contract
**Wave:** 2 (roadmap Phases 2 + 3)
**Depends on:** Wave 1
**Hot-file owner:** proto change is a **serial first step** (regenerate before others build); then one
owner of `RealtimeServer.cs` + `RealtimeEnvelopeMapper.cs` + `IGameSimulation.cs`. SDK + golden
fixtures build in a parallel worktree (new files).

## Context
The delta pipeline computes deltas but the wire only ever emits `ServerSnapshot`; documented
prediction/reconciliation can't actually happen. The game contract has no leave/terminate lifecycle.
There is no client SDK and no byte-level contract test. See gap analysis areas 1, 2, 5, 6, 8.

## Scope
- **Proto (serialized):** add/confirm `ClientLeaveRoom`, and wire `ServerDelta` /
  `ServerCorrection` / `ServerEvent`; regenerate `GameServer.Protocol` before downstream work.
- **Lifecycle hooks:** add `CanJoin`/`OnLeave`/`OnTerminate` (+ a server→game signal) to
  `IGameSimulation` with no-op defaults; call from `HandleJoinAsync`/`OnDisconnect`/reap.
- **Deltas on wire:** select `ServerSnapshot` vs `ServerDelta` in `TickRoom` fan-out and add the
  `MapServer` cases in `RealtimeEnvelopeMapper`; emit `ServerEvent` on join/leave/terminate.
- **Contract:** golden-packet fixtures (`contracts/realtime/fixtures/*`) + codec/compat test.
- **SDK:** generated TypeScript client from the `.proto` (parallel worktree).
- Decide + document `listRooms` / `reconnect-resume` (implement or explicitly defer in `PROTOCOL.md`).

## Tests required
- #2 `GoldenPacketRoundTrip` (contract): canonical envelopes encode to exact bytes + decode equal.
- #3 `DeltaRoundTrip` (unit/integration): delta applied to baseline == full snapshot.
- #10 `LeaveRoomFreesMembership` (integration): `ClientLeaveRoom` removes player + fires `OnLeave`.
- Per-hook unit tests (CanJoin reject path, OnTerminate on reap).

## Adversarial focus
Delta-applied-to-baseline == snapshot across multi-tick sequences; field-number/back-compat (no
reuse); unknown-message handling; reconnect/resume correctness; `ClientInputFrame` no longer
silently dropped (or documented as intentional).

## Acceptance criteria
Standard DoD + named tests green + a client can reconcile from deltas + the wire contract is
byte-pinned by fixtures + lifecycle hooks observable by a sample game.

## References
- Plan/spec: Appendix A §1, §2, §5, §6, §8; Test Gap Plan #2,#3,#10
- `RealtimeEnvelopeMapper.cs` (`MapServer`), `RealtimeServer.cs` (`TickRoom`),
  `contracts/realtime/proto/gameserver.realtime.v1.proto`, `IGameSimulation.cs`
