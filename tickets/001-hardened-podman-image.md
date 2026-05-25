# 001 — Hardened Podman image + scripts (the pentest target)

**Status:** open
**Area:** Host / container / ops
**Depends on:** prod-config hardening in `Program.cs` (done) and the WS frame-size bound (done)

## Context
The server is heading to AWS ECS. We need a realistic, hardened container artifact that
doubles as the target for the black-box pentest suite (ticket 002). It must boot in
`Production`, carry no secrets, run non-root, and refuse to start without a real join-token
secret and at least one control-plane API key (the fail-closed behavior already wired into
`Program.cs`).

## Scope
- **`Containerfile`** (repo root), multi-stage:
  - Build: `mcr.microsoft.com/dotnet/sdk:10.0`, `dotnet publish src/GameServer.Host -c Release`.
  - Runtime: chiseled/distroless `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled` (non-root `app` user by default).
  - `ENV ASPNETCORE_ENVIRONMENT=Production`, `ASPNETCORE_HTTP_PORTS=8080`, `EXPOSE 8080`.
  - No secrets baked in; no dev keys.
  - Note: `Program.cs` currently calls `builder.WebHost.UseUrls("http://localhost:5000")` — must bind `0.0.0.0:8080` in-container. Prefer honoring `ASPNETCORE_HTTP_PORTS`/`ASPNETCORE_URLS` over the hardcoded `UseUrls`, or override via env. Resolve while building this image.
- **`.containerignore`**: exclude `bin/`, `obj/`, `tests/`, `load/`, `.git/`, `tickets/`, plan files, etc.
- **`scripts/podman-build.ps1`**: `podman build -t citadel-host:local -f Containerfile .`
- **`scripts/security-test.ps1`**:
  - Build image, then `podman run` with: `-e Auth__JoinTokenSecret=<32+ char test secret>`, `-e ControlPlane__ApiKeys__0__Key=...` (+ CallerId/TenantId/Roles for tenant-a, tenant-b, and a platform-admin), `--read-only`, `--tmpfs /tmp`, `--cap-drop=ALL`, `-p 8080:8080`.
  - Poll `GET /ready` until green (bounded retries).
  - Export `CITADEL_SECURITY_TARGET`, `CITADEL_TEST_APIKEY`, `CITADEL_TEST_JOINSECRET` matching the injected values, then `dotnet test tests/GameServer.SecurityTests` (incl. `Category=Dos`).
  - Optional: `trivy image citadel-host:local` if `trivy` is on PATH (warn-only, non-fatal).
  - Always tear the container down (try/finally).

## Acceptance criteria
- `pwsh scripts/podman-build.ps1` produces an image that runs non-root.
- Container started WITHOUT `Auth__JoinTokenSecret` (or with the dev secret) fails to start / never reports `/ready` — proving the fail-closed wiring.
- Container in `Production` returns 404 for `/ws` and `/sim/telemetry`.
- `scripts/security-test.ps1` builds, boots, runs the suite green, and tears down cleanly.
- TLS trust boundary documented: container serves plaintext HTTP; TLS terminates at the ECS ALB.

## References
- Plan: `C:\Users\Brice\.claude\plans\im-also-wondering-about-declarative-pretzel.md`
- `src/GameServer.Host/Program.cs` (fail-closed secret + API-key wiring, dev-endpoint gate)
