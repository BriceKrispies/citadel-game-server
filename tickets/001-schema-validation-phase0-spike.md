# 001 — Phase 0: command-contract validation feasibility + perf spike

**Status:** open
**Area:** Validation (green-field) / benchmarking
**Depends on:** nothing (isolated; does NOT touch the live hot path)

## Context
Vision: anyone can build a Citadel game client by declaring a data contract; the server validates every
inbound command against it before it reaches the sim — fast and safe. Decided: protobuf contract,
**structural + value-constraint** validation (proto3 "parses" != "valid"), behind a pluggable
`ICommandValidator` seam. The #1 risk is per-command hot-path cost, so Phase 0 PROVES feasibility +
performance with a benchmark before any registry/admin/protocol work. There is no benchmark harness in the
repo yet, and C# `Google.Protobuf` has no first-class dynamic message — both are de-risked here.

## Scope
- Ports in `GameServer.Abstractions/Validation/`: `ValidationResult` (readonly struct, 0-alloc),
  `FieldConstraint` (int gte/lte, string max_len, enum set, required, repeated max_count), `CommandContract`
  (a compiled FileDescriptorSet + target message + constraints), `ICommandValidator`
  (`ValidationResult Validate(ReadOnlySpan<byte>)`), `ISchemaCompiler` (with a `MaxValidatorCost` budget that
  rejects pathological contracts at compile time).
- New project `GameServer.SchemaValidation` (rank 1; refs only Abstractions + Google.Protobuf). Spike BOTH:
  - **A) descriptor-guided wire-walker**: build a descriptor via `FileDescriptor.BuildFromByteStrings`,
    validate intent bytes by walking the wire format against it + constraints; no object materialization, no
    codegen, 0-alloc happy path.
  - **B) generated-types path**: parse into a build-time-generated message + constraint-check; the runtime
    codegen step (protoc/Roslyn) fail-fasts without protoc (a finding) — protoc is not on PATH here.
- `tests/GameServer.Benchmarks` (BenchmarkDotNet, `[MemoryDiagnoser]`, excluded from the fast loop):
  `CommandValidatorBenchmarks` (A vs B, small/large/array/string-heavy x valid/invalid) +
  `EnqueueHotPathBenchmarks` (`GameRoom.TryEnqueue` +/- validator).
- Contracts submitted as a compiled `FileDescriptorSet` (author runs `protoc --descriptor_set_out` / `buf build`).

## Budget / gate
- Validator < ~200 ns/command (large contract), **0 B/command**.
- Marginal hot-path cost (TryEnqueue+validate - TryEnqueue) < ~250 ns.
- Load p95 + tick p95 regression < 5% (ratio-based).

## Tests / acceptance
- Co-located unit tests (hermetic): valid passes; each constraint violation + unknown field/wire-type/oversize
  rejected with the right reason; deterministic.
- Benchmark RUNS and produces ns/op + alloc; findings note at `tickets/SCHEMA-VALIDATION-PHASE0-FINDINGS.md`
  with actuals vs budget + an A-vs-B recommendation + the dynamic-descriptor feasibility verdict.
- Build clean (analyzers as errors); fast loop stays green + hermetic; benchmark NOT in the fast loop.

## References
- Approved plan: `~/.claude/plans/i-want-a-full-piped-candle.md` (Phase 0).
- `src/GameServer.Simulation/GameRoom.cs` `TryEnqueue` (hot-path baseline); `src/GameServer.Protocol` (Google.Protobuf 3.22.5 / Grpc.Tools 2.54.0 build-time `<Protobuf>` pattern).
