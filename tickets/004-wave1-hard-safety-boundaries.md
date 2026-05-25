# 004 — Wave 1: hard safety boundaries

**Status:** open
**Area:** Transport / Tenancy / Observability (realtime hot path)
**Wave:** 1 (roadmap Phase 1)
**Depends on:** Wave 0 (done)
**Hot-file owner:** one agent owns `RealtimeServer.cs` + `Program.cs`

## Context
The kernel ships tenant-fairness/resource controls that are built and unit-tested but **dormant** —
not consulted on the hot path. This wave converts them from "class exists" to "enforced and proven,"
and replaces fake health with real readiness. See gap analysis areas 7, 10, 14.

## Scope
- Consult `TokenBucketTenantRateLimiter` (and optionally `FairTenantComputeBudget`) in
  `HandleCommandAsync`/tick path; shed over-rate tenants with `ServerError(Overloaded)`.
  (`src/GameServer.Tenancy/*`, `RealtimeServer.cs`, `Program.cs` DI.)
- Inbound **message-size limit** with a typed rejection; connection survives a single oversized
  frame. **Reconcile first** with the pentest effort's `Realtime:MaxFrameBytes` in
  `AspNetRealtimeChannel.cs` — don't duplicate; add the kernel-level guard only if that doesn't cover it.
- Real `/ready` dependency checks (telemetry sink, tenant resolver, snapshot store) replacing the
  static literal in `Program.cs`.
- Feed `TenantScopedMetrics` from the edge so global metrics can attribute load per tenant.

## Tests required
- #1 `TwoTenantNoisyNeighborLoad` (load/integration): one tenant floods; other tenant's reject
  rate/p95 unaffected. Add `load/scenarios/noisy-neighbor.json`.
- #7 `MessageSizeLimit` (unit): oversized frame rejected, connection survives.
- #8 `ReadinessProvesDependencies` (integration): `/ready` flips unhealthy on a downed dependency.
- #9 `PerTenantTelemetryAttribution` (unit/integration): per-tenant `messages_in` attributable.

## Adversarial focus
A tenant flood must not raise another tenant's reject rate/p95. Grep-assert every claimed-wired
control is actually referenced on the hot path (the dormant-seam failure mode this effort exists to fix).

## Acceptance criteria
Standard DoD (see `README.md`) + the four named tests green + the noisy-neighbor scenario proves
isolation + no remaining dormant-but-documented controls on the tenant path.

## References
- Plan/spec: `~/.claude/plans/greedy-marinating-toast.md` (Appendix A §7, §10, §14; Test Gap Plan #1,#7,#8,#9)
- `RealtimeServer.cs` (`HandleCommandAsync`), `Program.cs`, `Tenancy/*`, `Observability/TenantScopedMetrics.cs`
