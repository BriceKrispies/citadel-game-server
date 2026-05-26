# Tickets — pluggable game command-contract validation

Follow-up work for the schema-validation feature: a third party declares its command/intent data contract
(protobuf), the server validates every inbound command against it (structure + value constraints) before it
reaches the sim, and games go through an admin approval queue. Approved plan:
`~/.claude/plans/i-want-a-full-piped-candle.md`.

Dependency order: **001 (spike) -> go decision -> 002 + 003 -> 004**; 005 is an ergonomics follow-up to 001.

- [001 — Phase 0: feasibility + perf spike](001-schema-validation-phase0-spike.md) — `ICommandValidator` seam + wire-walker (A) vs generated-types (B) + BenchmarkDotNet; prove ns/op + 0-alloc before building the rest.
- [002 — Protocol: structured intent payload](002-protocol-structured-intent.md) — append-only `bytes intent` on ClientCommand (+ SDK + golden fixtures).
- [003 — Game-registration approval queue](003-game-registration-approval-queue.md) — registry status + stored contract (InMemory + Postgres), admin pending/approve/reject, realtime join gate.
- [004 — Wire the validator onto the hot path](004-wire-validator-hot-path.md) — per-command validation before the room lock + telemetry + perf gate.
- [005 — Constraint annotations](005-constraint-annotation-parsing.md) — derive FieldConstraints from protovalidate-style options on the descriptor.

> Note: the prior pentest/roadmap tickets (001-012) were deleted in `bdbfae4` ("all roadmap/pentest waves shipped"). This is a fresh set for the new feature.
