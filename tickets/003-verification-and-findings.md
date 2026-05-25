# 003 — Verification + living findings report

**Status:** open
**Area:** verification / docs
**Depends on:** 001, 002

## Context
Tie the work together: prove the hardened image + suite catch and lock down the four
confirmed findings, keep the fast loop green, and capture results in a living report.

## Confirmed findings to prove fixed
1. **Hardcoded dev API keys** in `Program.cs` — must not authenticate against the prod image.
2. **Dev-only endpoints** (`/ws` trusts query-string identity with no token; `/sim/telemetry` leaks telemetry) — must be 404 in `Production`. Now gated by `IsDevelopment()` AND `Realtime:EnableDevEndpoints`.
3. **Dev join-token secret fallback** — host now fails to start outside Development without a strong, non-default `Auth:JoinTokenSecret` (≥ 32 chars).
4. **Unbounded WS message buffering** in `AspNetRealtimeChannel` — now capped at `Realtime:MaxFrameBytes` (default 64 KiB); oversize messages drop the connection instead of growing memory to OOM.

## Verification steps
- Fast loop unchanged & green: `dotnet test tests/GameServer.Tests/GameServer.Tests.csproj` (security suite skips with no target).
- Full build incl. analyzers: `dotnet build Citadel.slnx` (CITADEL0001/0002/0003 — new test project must be ranked/exempt; security suite is a test project).
- End-to-end: `pwsh scripts/security-test.ps1` → builds image, boots hardened container with test secrets, waits `/ready`, runs `dotnet test tests/GameServer.SecurityTests` (incl. `Category=Dos`), optional `trivy`, tears down.
- Manual spot checks: `curl /ws` & `curl /sim/telemetry` → 404; `curl -H "Authorization: Bearer dev-admin-key" /api/v1/admin/rooms` → 401; container with no/weak `Auth__JoinTokenSecret` never reports `/ready`.

## Deliverable
- **`tests/GameServer.SecurityTests/FINDINGS.md`** — living pentest report: each finding with severity, the repro test that pins it, and status (fixed / accepted-gap). Include the per-tenant rate-limit / compute-budget wiring gap as a tracked accepted gap.

## References
- Plan: `C:\Users\Brice\.claude\plans\im-also-wondering-about-declarative-pretzel.md`
