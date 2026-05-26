# 005 — Derive field constraints from protovalidate-style annotations

**Status:** open
**Area:** Validation
**Depends on:** 001 (contract model + compiler)

## Context
Phase 0 supplies `FieldConstraint`s EXPLICITLY alongside the descriptor. The ergonomic end state is that the
game author declares constraints inline in the `.proto` (protovalidate-style options, e.g.
`int32 power = 2 [(buf.validate.field).int32 = {gte:0, lte:100}]`) and the server DERIVES the constraint set
by reading those custom options off the submitted `FileDescriptorSet` — so the contract is a single artifact.

## Scope
- Read protovalidate (or a minimal custom) field options from the descriptor's `CustomOptions` and map them to
  the `FieldConstraint` model. Reject unsupported/over-budget constraints at compile time.
- Decide the supported subset (int range, string max_len, enum-in-set, required, repeated max_count); document it.
- Keep the explicit-constraint path working (tests/tools may still pass constraints directly).

## Tests / acceptance
- A `.proto` with annotations compiles to the same validator as the equivalent explicit `FieldConstraint`s.
- Unsupported/oversize annotations are rejected with a clear error at registration.

## References
- Approved plan: `~/.claude/plans/i-want-a-full-piped-candle.md` (Phase 0 "deferred: annotation parsing").
- protovalidate field options; `GameServer.SchemaValidation` `ISchemaCompiler`.
