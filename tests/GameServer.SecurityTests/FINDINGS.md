# Citadel - Black-box pentest findings (living report)

This is the living security report for the Citadel host container. Each finding lists its severity,
the repro test that pins it, and its status. The suite that pins these
(`tests/GameServer.SecurityTests`) drives the **running hardened container** over real HTTP +
WebSocket. It is NOT in the fast unit loop: every test skips unless `CITADEL_SECURITY_TARGET` is
set, so a plain repo-wide `dotnet test` stays green. Run it end-to-end with:

```
pwsh scripts/security-test.ps1
```

That script builds the image, boots a locked-down container (`--read-only`, `--cap-drop=ALL`,
`--tmpfs /tmp`), injects a strong join secret + control-plane API keys, waits for `/ready`, and runs
the suite (including the `Category=Dos` tests).

## TLS trust boundary

The container serves **plaintext HTTP** on `:8080`. In production TLS terminates at the AWS ECS ALB
and decrypted HTTP is forwarded to the task, so the suite tests over `http://` on purpose - it
exercises the same listener the ALB targets.

## Confirmed findings (all fixed)

### Finding 1 - Hardcoded dev API keys must not authenticate against the prod image
- **Severity:** High (authentication bypass)
- **Detail:** `Program.cs` seeds `dev-tenant-a-key`, `dev-tenant-b-key`, `dev-admin-key` only in the
  Development branch. A Production image must reject them and require configured
  `ControlPlane:ApiKeys`.
- **Pinned by:** `ControlPlaneAuthTests` - the `HardcodedDevKeys_AreRejected` theory (all three dev
  keys -> 401), plus the missing / garbage / wrong-scheme / raw-key 401 matrix and the
  `ConfiguredTenantKey_Authenticates` positive control.
- **Status:** Fixed (dev keys exist only under `IsDevelopment()`; prod fails closed if no keys are
  configured).

### Finding 2 - Dev-only endpoints must be 404 in Production
- **Severity:** High (`/ws` trusts query-string identity with no token; `/sim/telemetry` leaks
  aggregate telemetry).
- **Detail:** Both are gated by `IsDevelopment() AND Realtime:EnableDevEndpoints`, so a Production
  host never maps them.
- **Pinned by:** `ContainerPostureTests.DevWebSocketEndpoint_Is404`,
  `ContainerPostureTests.DevTelemetryStream_Is404`, plus `/health` `/ready` `/version` disclosure
  review, response-header hygiene, and an unknown-path 404 (no developer exception page).
- **Status:** Fixed.

### Finding 3 - Dev join-token secret fallback / forgeable tokens
- **Severity:** Critical (join-token forgery -> impersonate any tenant/room/player).
- **Detail:** The host fails to start outside Development without a strong, non-default
  `Auth:JoinTokenSecret` (>= 32 chars). The realtime edge verifies HS256 statelessly and must reject
  any token not signed with that secret.
- **Pinned by:** `JoinTokenAttackTests` - forged signature (wrong secret), tampered payload, expired,
  `alg=none`, alg-swap, missing, and garbage tokens all rejected (the WS upgrade fails); the
  `ValidToken_IsAccepted` positive control proves the edge accepts a genuinely-minted token.
- **Status:** Fixed.

### Finding 4 - Unbounded WS message buffering (single-connection OOM)
- **Severity:** High (DoS).
- **Detail:** `AspNetRealtimeChannel` now caps a single reassembled message at
  `Realtime:MaxFrameBytes` (default 64 KiB); an oversize/never-ending message drops the connection
  instead of growing memory toward an OOM kill.
- **Pinned by:** `ProtocolFuzzTests.OversizeFrame_IsDroppedWithoutCrash` (1 MiB frame -> clean drop,
  `/ready` still 200). This is the end-to-end coverage for the frame-size bound; there is no
  co-located unit test because `GameServer.Tests` does not reference the Host.
- **Defense-in-depth (Wave 1):** the transport frame cap drops oversize WS frames at the socket and
  closes that connection. Behind it, the kernel now applies a SECOND, finer bound on the decoded
  command size (`RealtimeServer` `maxCommandBytes`, `Realtime:MaxCommandBytes`, defaulting to the
  frame cap): an over-bound command is shed with a typed `ServerError(MalformedMessage)` and the
  connection SURVIVES — a single bad command is not a disconnect. This covers paths the WS frame cap
  does not (the in-memory transport, and any decoded payload that slipped under the frame budget).
  Pinned by `RealtimeServerSafetyTests.OversizedCommand_IsRejected_AndConnectionSurvives`.
- **Status:** Fixed.

## Cross-tenant isolation (defense-in-depth, verified)

- **Control plane:** a tenant-A key against a tenant-B room / mint / session / admin-observe -> 403
  (`TenantIsolationTests`).
- **Realtime edge:** a connection holding a tenant-A token that declares tenant-B / another game /
  another room / another player in its handshake -> `ServerError(Unauthorized)`, no state access
  (`TenantIsolationTests.CrossTenantJoinOverWebSocket_IsUnauthorized`,
  `RealtimeScopeEscalationTests`).

## Protocol robustness (verified)

`ProtocolFuzzTests`: random/truncated protobuf, a text frame on the binary channel, and an
unsupported protocol version are all shed cleanly (typed error and/or clean close) and the server
re-probes `/ready == 200` afterward - it stays up.

## DoS / backpressure (verified, bounded)

`DosBackpressureTests` (`Category=Dos`): a bounded connection flood, a bounded room flood, a command
flood past the 1024 room queue depth, and a slowloris (silent connection vs the 1s handshake
timeout) all shed gracefully (typed error / clean close / reap) and the server stays ready. Counts
and durations are bounded so the suite cannot hang or exhaust the host; the absolute production
ceilings (100k connections, etc.) are not driven to their limit on a single test box.

## Tenant fairness

### Gap A - Per-tenant rate limiting (FIXED, Wave 1)
- **Severity:** Medium (noisy-neighbour / fairness).
- **Detail:** `TokenBucketTenantRateLimiter` (`GameServer.Tenancy`) is now WIRED into the realtime
  command path: `RealtimeServer.HandleCommandAsync` meters every inbound command against the
  sender's per-tenant token bucket BEFORE it reaches the room queue, and sheds the overflow as
  `ServerError(Overloaded)` → `ERROR_CODE_BACKPRESSURE_REJECTED` on the wire (there is no distinct
  `RATE_LIMITED` kernel code; backpressure is the honest, retryable code). The bucket reads time
  through an injected `IMonotonicClock` so refill is deterministic under test. Buckets are
  independent per tenant, so one tenant's flood cannot consume another tenant's allowance.
- **Pinned by:**
  - Black-box: `AcceptedGapTests.PerTenantRateLimiting_ShedsASustainedSingleTenantFlood` (a sustained
    single-tenant flood is shed with `BACKPRESSURE_REJECTED` and the server stays `/ready`).
  - In-process: `NoisyNeighborScenario.OneTenantFlood_DoesNotRaiseAnotherTenantsRejectRate`
    (integration) and `RealtimeServerSafetyTests` (unit).
  - Load: `load/scenarios/noisy-neighbor.json` (noisy tenant-a) run concurrently with
    `noisy-neighbor-quiet.json` (tenant-b) — tenant-a is throttled (hundreds of thousands of
    backpressure errors) while tenant-b's reject rate stays **0** and its p95 is flat.
- **Status:** Fixed.

### Gap B - Per-tenant compute budget not wired to the tick path (still tracked)
- **Severity:** Medium (fairness under load).
- **Detail:** `FairTenantComputeBudget` (`GameServer.Tenancy`) is built and unit-tested
  (`PerTenantComputeBudgetScenario` proves the fair-share math) but is NOT yet gating
  `RoomTickService`, so there is no per-tenant compute fairness applied under load. Wiring it cleanly
  needs tenant-ordered tick scheduling and was deliberately left out of Wave 1's scope rather than
  shipped as a half-real control.
- **Tracked by:** `AcceptedGapTests.PerTenantComputeBudget_IsNotYetWiredToTheTickPath` (skipped with
  this reason). Revisit once the budget gates the tick scheduler.
- **Status:** Accepted gap (deferred from Wave 1).

## Finding 5 - Wire error-code fidelity: authz rejections surfaced as INTERNAL_SERVER_ERROR
- **Severity:** Low (information fidelity / observability; NOT an authorization bypass).
- **Detail:** When the realtime kernel rejected a scope-escalation or cross-tenant handshake it
  emitted `ServerErrorCode.Unauthorized`, but `RealtimeEnvelopeMapper.MapErrorCode` had no case for
  `Unauthorized` (nor `Overloaded`) and fell through to `ERROR_CODE_INTERNAL_SERVER_ERROR`. The
  authorization decision was always CORRECT — access denied, no state leaked — but the client/log
  saw a misleading "internal server error" instead of "unauthorized".
- **Discovered by:** `RealtimeScopeEscalationTests` / `TenantIsolationTests` over the wire (expected
  `UNAUTHORIZED`, observed `INTERNAL_SERVER_ERROR`).
- **Fix:** `RealtimeEnvelopeMapper.MapErrorCode` now maps `ServerErrorCode.Unauthorized ->
  ErrorCode.Unauthorized` and `ServerErrorCode.Overloaded -> ErrorCode.BackpressureRejected`, pinned
  by the co-located `RealtimeEnvelopeMapper.Tests.cs` (full kernel->wire mapping table). The wire
  suite now asserts the honest code: `TenantIsolationTests.CrossTenantJoinOverWebSocket_IsUnauthorized`
  asserts `ErrorCode.Unauthorized`, and `RealtimeScopeEscalationTests` asserts any returned code is
  `Unauthorized` (a clean close with no ServerError frame still denies access and is acceptable).
- **Status:** Fixed.
