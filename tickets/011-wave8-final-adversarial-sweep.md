# 011 — Wave 8: final cross-cutting adversarial sweep

**Status:** open
**Area:** Whole system (red-team, boundary-ignoring)
**Wave:** 8 (roadmap final gate)
**Depends on:** Waves 1–7

## Context
Per-wave adversarial passes attack one workstream's diff. This final sweep ignores workstream
boundaries and attacks the integrated system as a whole, then verifies the docs finally match wired
reality (the recurring "claims vs reality" smell from the gap analysis).

## Scope (red-team, then fix criticals)
- **Tenant-isolation penetration**: attempt cross-tenant enumeration, fanout, snapshot/replay access
  via every endpoint and channel; confirm `RoomKey` scoping holds end-to-end.
- **Protocol fuzzing + golden drift**: malformed/oversized/out-of-order frames; verify golden
  fixtures still hold and back-compat field rules are intact.
- **Replay determinism**: snapshot+events reproduce identical state across a fresh process, with seed.
- **Combined-load backpressure**: flood + slow consumers + many rooms + admin-under-load together;
  no tenant's SLO violated; degradation ladder monotonic.
- **End-to-end correlation IDs**: a single id propagates connect → join → intent → fanout → error and
  appears in logs/traces.
- **Claims-vs-reality doc audit**: `CLAUDE.md` / `ARCHITECTURE.md` / `PROTOCOL.md` describe only what
  is wired; no dormant seam presented as a feature.

## Deliverables
- A **final gap report** (severity-ranked) + RED tests for any open gaps.
- All **critical/high** defects fixed and re-verified.

## Acceptance criteria
Standard DoD across the **full** `Citadel.slnx` (unit + integration + analyzer + infra-gated tests) +
zero open critical/high adversarial findings + the doc audit confirms no remaining over-reporting +
end-to-end correlation context demonstrated.

## References
- Plan/spec: `~/.claude/plans/greedy-marinating-toast.md` (Architectural Smells, Missing Invariants)
- All prior wave tickets (004–010)
