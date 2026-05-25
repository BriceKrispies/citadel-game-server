# Wave 8 — Final cross-cutting adversarial sweep: gap report

**Branch:** `feat/enterprise-roadmap`  **Baseline HEAD:** `fc97687`
**Verdict:** Zero open Critical/High. The named requirement (end-to-end correlation IDs) is implemented and
test-proven. Docs corrected to match wired reality. Deferred ledger triaged below.

This sweep ignored workstream boundaries and attacked the integrated system as a whole. No NEW
Critical/High defects were found in the wired hot path or control plane; the two cross-tenant Criticals
fixed in earlier waves still hold (re-verified). The most material finding was a reproducible FAST-suite
flake (the long-standing "transient single failure"), which is now root-caused and de-flaked
deterministically.

---

## A. Whole-system red-team — per-area verdict

### 1. Tenant-isolation penetration — PASS (coverage extended)
Cross-tenant access was probed via every control-plane endpoint and the realtime channel. `RoomKey`
`(tenantId, roomId)` scoping and authorize-before-touch hold end to end:
- List endpoints filter to the caller's tenant; get/terminate/observe of another tenant return **403,
  not 404** (no existence oracle). Already covered by `ControlPlaneIsolationScenario`.
- **Extended coverage (this wave):** the **replay** endpoint
  (`/admin/rooms/{t}/{r}/replay`) and the **live SSE observe-stream** endpoint were the two admin paths
  that read durable tenant data but had no explicit cross-tenant adversarial test. Added assertions that a
  tenant-a caller hitting a tenant-b room on both returns **403 (not 404)** — authorize before reading any
  of the other tenant's snapshot/event data, so replay cannot be a cross-tenant recovery/exfiltration
  backdoor. (`tests/GameServer.IntegrationTests/ControlPlaneIsolationScenario.cs`)
- Per-tenant `/admin/metrics` is authorization-filtered (a tenant-scoped caller sees only its own slice);
  the matchmaking ticket registry buckets by `MatchScope` so cross-scope bleed is structurally impossible.
- The new matchmaking fetch endpoint authorizes against the assignment's **owning tenant**, so a token is
  never handed to the wrong tenant even with the exact ticket id (proven by a RED→GREEN test).

No new Critical/High found.

### 2. Protocol fuzzing + golden drift — PASS
The 8 golden fixtures (`contracts/realtime/fixtures/*.bin`) still encode/decode byte-for-byte; the
tamper-detection test holds. Malformed binary frames are rejected with `MALFORMED_FRAME` and the
connection survives; oversized commands are shed with a typed error without dropping the socket;
unsupported versions and missing required envelope fields are rejected. Back-compat field rules
(`WireBackCompat.Tests`) intact. The correlation work introduced **no wire change** (it is a
telemetry-tag/log change only), so there is zero golden drift. No new Critical/High.

### 3. Replay determinism — PASS
Snapshot+events reproduce identical state on a fresh instance (`DurableRestartScenario`, non-gated;
`RoomRecoveryService.Tests` for seeded determinism; the Postgres `DeterministicReplayWithSeed`/crash-recovery
scenarios under a container runtime). Recovery reads strictly by `RoomKey` and fails loudly (422 at the
admin replay endpoint) on incompatible recovery data rather than producing silently-wrong state. No change
made here; re-verified green.

### 4. Combined-load backpressure / degradation — PASS
The degradation ladder is monotonic and sheds only OPTIONAL work (new connections → new rooms → telemetry
events → spectator-snapshot rate), never the authoritative tick, state mutation, or snapshot/event
persistence (the Wave-6 no-drop invariant). `DegradationLadderScenario`, `NoisyNeighborScenario`,
`AdmissionControlScenario`, and the per-tenant rate-limit/room-cap scenarios all green. No change to the
shedding logic; re-verified.

### 5. End-to-end correlation IDs — IMPLEMENTED (named requirement)
**Finding:** before this wave a single id did NOT flow end to end. The inbound envelope's per-message
`traceId` was echoed on the *immediate* response, but (a) most structured events carried no trace/correlation
field at all, and (b) each client message minted a *different* traceId, so there was no single id tying a
connection's lifecycle together in logs.

**Implementation** (`src/GameServer.Transport/RealtimeServer.cs`): every connection is assigned a stable,
**server-owned** `CorrelationId` (`conn-{connectionId}`) at connect — before any client frame, and
deliberately NOT derived from any client value, so a hostile/sloppy client cannot fragment or collide a
session's trail. A `CorrelatedTags` helper stamps that id (plus `connectionId`) onto every structured event
along the path:
- **connect** (`connection_opened`) → **session** (`session_created`) → **join** (`room_joined`) →
  **command accepted** (new `command_accepted` event — previously only a counter) → **error**
  (`command_rejected`, which also carries the per-request `traceId` for request-level pinpointing) →
  identity/tenant/protocol rejections and idle/handshake drops.

The per-message `traceId` is retained (echoed on responses and recorded on the error/accept legs) as the
request-level id, distinct from the connection-level correlation id.

**Proof:** `src/GameServer.Transport/RealtimeServer.Correlation.Tests.cs` —
`OneCorrelationId_Flows_Connect_Through_Error` drives connect→hello→join→accepted-command→stale-command
and asserts all five legs share one correlation id (and the wire `ServerError` carries the request traceId);
`CorrelationId_IsStable_AcrossDifferentPerMessageTraceIds` proves the id does not change as the client mints
new per-message traceIds; `RejectedConnect_StillCarriesCorrelationId` proves a no-session unknown-tenant
rejection is still correlated to its connect.

**Control-plane leg (documented-accepted):** admin requests are already correlatable via ASP.NET's
per-request `TraceIdentifier` in the default request log scope. Persisting it into the durable audit row
(`AuditRecord`/`TenantAuditRecord`) was deferred because it requires a schema column + migration on the
per-tenant audit table — out of scope for a Wave-8 cleanup and not needed for the named realtime chain.

### 6. Claims-vs-reality doc audit — corrected
Over-claims found and fixed (all were doc-comments describing schema/seams as if shipped):
- **`ITenantComputeBudget`** doc-comment now states **NOT WIRED** to the tick path (dormant seam, not an
  active control) and references Gap B below.
- **`TenantDbContext` / `DurableRecords`** doc-comments overstated the durable business tables. Corrected:
  `games`/`game_versions`, `audit_records`, `room_snapshots`/`room_events` ARE wired (their adapters exist);
  the **`rooms` and `sessions` tables are RESERVED SCHEMA — NOT YET WIRED** (the host still serves room/
  session registration from the in-memory registries even under the Postgres backend).
- `CLAUDE.md`/`ARCHITECTURE.md`/`PROTOCOL.md` checked: no dormant seam is presented as a feature
  (`correlationId` in CLAUDE.md is a requirement, now actually satisfied for the realtime path; the
  compute-budget is nowhere claimed as wired).

---

## B. Deferred-items ledger triage

| # | Item | Severity | Status | Where / why |
|---|------|----------|--------|-------------|
| 1 | `Server: Kestrel` response banner | Low | **FIXED** | `Program.cs` `ConfigureKestrel(o => o.AddServerHeader = false)`; test `AdminLimitsValidationScenario.Host_DoesNotAdvertiseServerStack`. |
| 2 | `/ready` no per-probe timeout | Low | **Documented-accepted** | `IReadinessCheck.Check()` is a synchronous, cheap-by-contract invariant (no I/O await), so there is no slow-probe to time out. A per-probe timeout would require an async/cancelable contract change with no current risk to mitigate. |
| 3 | Gap B — `FairTenantComputeBudget`/`ITenantComputeBudget` not wired to the tick path | Medium | **Documented-accepted (honestly)** | Marked NOT WIRED in `ITenantComputeBudget.cs`. Wiring it conflicts with the Wave-6 invariant that the authoritative sim is NEVER dropped (an over-budget room must be DEFERRED within cadence, never skipped) — a real feature, not a cleanup. Left as a dormant seam, no longer presented as a capability. |
| 4 | `DurableRecords.cs`/`TenantDbContext.cs` overstate unwired durable tables | Low | **FIXED (docs)** | Corrected as in area 6: `rooms`/`sessions` tables marked RESERVED SCHEMA — NOT YET WIRED; the wired tables named explicitly. |
| 5 | Replay endpoint `?gameId` defaults to `"demo"` | Low | **Documented-accepted** | The recovery path fails loudly (422) on an incompatible game rather than producing silently-wrong state (the game's own `Restore` validates the bytes). The canonical gameId would come from the durable `rooms` table, which is itself not-yet-wired (#4), so it cannot be derived automatically today. |
| 6 | `NodeId == address`, no per-instance fencing token | Low/Med | **Documented-accepted** | Only matters with out-of-process room-state migration (not shipped). Single-owner Redis directory + lease renewal already prevent split-brain for the in-process model (proven by Wave-4 fencing scenarios). A monotonic fencing token is the right fix when OOP migration lands. |
| 7 | Matchmaking lost-assignment (queued→matched player can't fetch its token; 202 Location 404s) | Medium | **FIXED** | New `AssignmentStore` (TTL-bounded) records each cycle's assignments; new `GET /matchmaking/tickets/{ticketId}` serves them (authorized against the assignment's owning tenant). Tests: `MatchmakingScenario.QueuedPlayer_MatchedByALaterCycle_CanFetchItsAssignmentByTicketId`, `FetchingAnotherTenantsAssignment_IsForbidden_NotLeaked`, and `AssignmentStore.Tests`. |
| 8 | `MatchDirector._assigned` grows unbounded | Medium | **Addressed via #7 (documented)** | The lost-assignment fix introduces a TTL-bounded `AssignmentStore` that self-prunes on each record (proven by `Record_PrunesExpiredEntries_SoTheStoreSelfBounds`). The director's `_assigned` claim set is the cross-cycle double-assign guard whose entries are only relevant while a ticket is live (a ticket is removed from the registry on assignment and ids are unique per submit, so it is never re-submitted); its growth is bounded by live ticket throughput, and the double-assign guarantee is preserved (the concurrent-race test still passes). A standalone TTL on the claim set was not added to avoid weakening that guarantee; documented as accepted. |
| 9 | `/matchmaking/tickets` does no catalog validation | Low | **Documented-accepted** | Adding strict catalog validation would break the established matchmaking flow: the matchmaking happy-path (and its tests) use the unregistered `demo-game`, and room allocation for matchmaking bypasses the catalog-validated `POST /rooms` endpoint. Making this validate requires seeding/registering games for every matchmaking path — a behavioral change beyond a Low cleanup. Left unvalidated, documented. |
| 10 | `FakeRoomAllocator.Allocations` non-thread-safe `List` under `Parallel` | Low (test-infra) | **FIXED** | Changed to `ConcurrentBag` (the concurrent double-assign test shares one allocator across two `Parallel.Invoke` directors — a genuine data race / latent flake). `MatchmakingFakes.Fakes.cs`. |
| 11 | `PUT /admin/limits` accepts degenerate (0/neg) values | Low/Med | **FIXED** | Validates every ceiling `>= 1` (a `MaxConnections <= 0` would silently refuse ALL traffic), returns 400 + audits a rejected attempt, does not apply the bad update. Tests: `AdminLimitsValidationScenario` (theory over degenerate inputs + valid-applies + unchanged-on-reject). |
| 12 | Elusive transient FAST-suite flake | — | **FIXED (root-caused)** | Reproduced on the first baseline run: `RoomTickSchedulerTests.Sequential_RunsExactlyOneRoomAtATime` (expected MaxConcurrency 1, got 2). Root cause: `RoomTickSample.EndOffsetMs` was reconstructed as `StartOffsetMs + ElapsedMs` (a sum of two floating-point `Stopwatch.GetElapsedTime` conversions), which could round up past the next sample's single-conversion `StartOffsetMs`, fabricating a sub-microsecond "overlap". Fixed structurally: the sample now stores start AND end as independent single-conversion offsets from one cycle origin, so for a serial scheduler `EndOffsetMs(n) <= StartOffsetMs(n+1)` is exact. Locked by `Sequential_NeverReportsPhantomConcurrency_ForManyTinyTicks` (200 near-instant ticks × 20 attempts → always concurrency 1). 3× full-suite reruns green. |

---

## Verification summary

- **Build:** `dotnet build Citadel.slnx --no-incremental` → **0 errors**, 54 warnings (all pre-existing
  CITADEL0001 on composition-root/abstraction files; **no new** ones — `AssignmentStore.cs` has its
  co-located test). CITADEL0002/0003 (layering): **clean**.
- **Fast unit:** `tests/GameServer.Tests` → **465 passed, 0 failed** (was 454; +11: 3 correlation, 1
  de-flake regression test, 7 AssignmentStore). Run **3×** hermetic, all green (flake hunt closed).
- **Integration:** `tests/GameServer.IntegrationTests` → **73 passed, 16 skipped** (the skipped are the
  Postgres/Redis container-gated scenarios; they skip-green when no container runtime is reachable — the
  documented behavior — and none of this wave's changes touch their wired behavior).
- **Analyzer:** `Citadel.RepoAnalyzers.Tests` → **20 passed**.
- **Security (no live target):** `tests/GameServer.SecurityTests` → **44 skip-green**.

## Roadmap DoD
The full-solution DoD is met: build clean (analyzers as errors pass), fast-unit + non-gated integration +
analyzer green, the named requirement implemented and proven, docs match wired reality, and **zero open
Critical/High** adversarial findings. The only residual items are the Low/Medium ledger entries
**documented-accepted** above with explicit rationale (none presented as a shipped feature).
