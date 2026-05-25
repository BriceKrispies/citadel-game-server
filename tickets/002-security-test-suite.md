# 002 — Black-box security/pentest suite (`tests/GameServer.SecurityTests`)

**Status:** open
**Area:** tests / security
**Depends on:** 001 (a running hardened container to attack)

## Context
The existing `tests/GameServer.IntegrationTests` are white-box, in-process (in-memory
transports via `IntegrationHarness`) and cannot catch real-host/over-the-wire or
config-misconfiguration issues. This suite drives the RUNNING container over real HTTP +
WebSocket and locks down the attack surface, including the four confirmed findings.

## Scope
- New xunit project `tests/GameServer.SecurityTests/` — NOT in the fast unit loop.
  - References `GameServer.Protocol` (real `RealtimeProtobufCodec`/`RealtimeEnvelopeMapper` to build valid + malformed frames) and `GameServer.Identity` (mint wrong-secret tokens for forgery tests).
  - Packages: `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`, `Xunit.SkippableFact`.
  - Add to `Citadel.slnx`.
- **Target via env** `CITADEL_SECURITY_TARGET` (+ `CITADEL_TEST_APIKEY`, `CITADEL_TEST_JOINSECRET`). If unset → tests SKIP (so repo-wide `dotnet test` stays green). The `security-test.ps1` script (ticket 001) sets them.
- Positive controls go fully black-box: get a real join token via `POST /api/v1/rooms/{id}/join-token` with the configured key, then connect with `ClientWebSocket`.

## Test classes (one concern each)
- `ControlPlaneAuthTests` — 401 matrix (missing / garbage / wrong-scheme / **the old `dev-*` keys** must all fail against the prod image).
- `TenantIsolationTests` — tenant-A key vs tenant-B room/session/admin-observe → 403; cross-tenant join over WS → `Unauthorized`.
- `JoinTokenAttackTests` — forged signature, tampered payload, expired, `alg=none`/alg-swap, missing token → 401/reject.
- `RealtimeScopeEscalationTests` — valid token, then `ClientHello`/`ClientJoinRoom` declaring a different tenant/room/player → `ServerError` (`Unauthorized`), no state access.
- `ProtocolFuzzTests` — random/truncated protobuf, text frame on a binary channel, unsupported version, **oversize frame (> `Realtime:MaxFrameBytes`)** → clean close, server stays up (re-probe `/ready`). NOTE: the oversize-frame case is the end-to-end coverage for the WS frame-size bound (no co-located unit test, since `GameServer.Tests` does not reference the Host).
- `DosBackpressureTests` — `[Trait("Category","Dos")]`, opt-in. Connection flood vs `MaxConnections`/per-tenant, room flood vs per-tenant cap, command flood vs queue depth (1024), slowloris vs the 1s handshake timeout. Assert graceful shedding (`ServerError`/close), not crash.
- `ContainerPostureTests` — `/ws` & `/sim/telemetry` → 404; `/health`,`/ready`,`/version` info-disclosure review; response-header hygiene.

## Acceptance criteria
- Suite skips cleanly when no target env is set; runs green against the hardened container.
- All four findings proven fixed (dev keys rejected; dev endpoints 404; forged/dev-secret tokens rejected; oversize frame dropped without OOM).
- Known accepted gap documented as a skipped/`xfail` test: per-tenant rate limiting / compute budget (`TokenBucketTenantRateLimiter`, `FairTenantComputeBudget`) are built but **not wired** to the hot path.

## References
- Plan: `C:\Users\Brice\.claude\plans\im-also-wondering-about-declarative-pretzel.md`
- Harness pattern: `tests/GameServer.IntegrationTests/HostAdmissionCapScenario.cs`
- Surfaces: `RealtimeServer.HandleHelloAsync/HandleJoinAsync`, `Hs256JoinTokenCodec`, `ApiKeyControlPlaneAuthenticator`, `CallerPrincipal.CanActFor`
