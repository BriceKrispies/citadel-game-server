# 004 — Wire the per-game command validator onto the hot path

**Status:** open
**Area:** Transport (realtime hot path) / Observability
**Depends on:** 002 (intent payload), 003 (validator compiled + cached per game)

## Context
With a validator compiled at approval and cached on the connection (gameId is stable per connection), enforce
it on every inbound command BEFORE the command reaches the room — rejecting anything outside the declared
contract, fast and allocation-free.

## Scope
- `Connection` gains a cached `ICommandValidator? Validator` (resolved once at join).
- `RealtimeServer.HandleCommandAsync`: after the size guard, BEFORE `lock (room)`/`TryEnqueue`, run
  `validator.Validate(command.Intent.Span)`; on failure reject with a typed `ServerError`
  (`InvalidCommand`) — the connection survives. Schema-less games / empty intent skip it (zero added cost).
- Telemetry: new `invalid_payloads` counter (aggregated allocation-free), tagged by gameId.
- Perf gate: re-run the Phase 0 benchmark against the wired path; assert the budget holds under load
  (`InputToSnapshotLatency.p95` + `tick_duration_ms` from `/admin/metrics`), ratio-based.

## Tests / acceptance
- Unit: a payload violating the contract is rejected before enqueue; a valid one is enqueued; connection
  survives a bad frame; schema-less game unaffected.
- The wired hot path holds the Phase 0 budget (0-alloc per command; p95 regression < 5%).

## References
- Approved plan: `~/.claude/plans/i-want-a-full-piped-candle.md` (Phase 1).
- `src/GameServer.Transport/RealtimeServer.cs` (`HandleCommandAsync`, `Connection`), `src/GameServer.Observability/TelemetryMetrics.cs`.
