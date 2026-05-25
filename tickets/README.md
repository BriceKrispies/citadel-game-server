# Tickets

Outstanding work for the containerize + pentest effort. See the approved plan at
`C:\Users\Brice\.claude\plans\im-also-wondering-about-declarative-pretzel.md` for full context.

Already done (code complete, in this branch):
- Production-config hardening in `src/GameServer.Host/Program.cs` (fail-closed join secret + API keys; dev-endpoint opt-in gate).
- WS message-size bound in `src/GameServer.Host/AspNetRealtimeChannel.cs` (`Realtime:MaxFrameBytes`).

Open tickets:
- [001 — Hardened Podman image + scripts](001-hardened-podman-image.md)
- [002 — Black-box security/pentest suite](002-security-test-suite.md)
- [003 — Verification + living findings report](003-verification-and-findings.md)
- [012 — Run the integration suite's containers on Podman (chore)](012-integration-suite-podman.md)

---

# Enterprise Roadmap (gap-analysis waves)

Plan: `C:\Users\Brice\.claude\plans\greedy-marinating-toast.md` (Appendix A = gap analysis = spec).
Branch: `feat/enterprise-roadmap`. Builder + adversarial agent team; one wave per ticket, each with
an adversarial gate and the standard Definition of Done (build clean — analyzers are build errors —
fast-unit + integration green, wave tests green, adversarial defects closed, docs match reality,
user gate).

Done:
- **Wave 0 — Foundations & guardrails** ✅ (commit `73ca111`): branch + corrected stale RED-phase
  doc-comments (`AdmissionPolicy`, `IIdleConnectionPolicy`).

Open tickets (dependency order 1 → 2 → (3 ∥ 4) → 5 → 6 → 7 → 8):
- [004 — Wave 1: hard safety boundaries](004-wave1-hard-safety-boundaries.md)
- [005 — Wave 2: room lifecycle, protocol, deltas, SDK](005-wave2-lifecycle-protocol-deltas-sdk.md)
- [006 — Wave 3: DB-per-tenant persistence + replay](006-wave3-db-per-tenant-replay.md)
- [007 — Wave 4: allocation, capacity, multi-node](007-wave4-allocation-multinode.md)
- [008 — Wave 5: matchmaking](008-wave5-matchmaking.md)
- [009 — Wave 6: load/failure conformance + degradation](009-wave6-load-failure-conformance.md)
- [010 — Wave 7: admin/control-plane hardening](010-wave7-admin-controlplane-hardening.md)
- [011 — Wave 8: final cross-cutting adversarial sweep](011-wave8-final-adversarial-sweep.md)

> Overlap note: the pentest effort above reports a **WS message-size bound**
> (`Realtime:MaxFrameBytes` in `AspNetRealtimeChannel.cs`) and **fail-closed prod config** already
> done. Wave 1 / Wave 7 must reconcile with that work rather than duplicate it.
