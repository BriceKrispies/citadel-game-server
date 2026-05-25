# 007 — Wave 4: allocation, capacity, multi-node

**Status:** open
**Area:** Routing / Cluster.Redis / Host (clustered)
**Wave:** 4 (roadmap Phase 5 + wire existing seams)
**Depends on:** Wave 0 (independent of 2/3)
**Isolation:** cluster work in a worktree; `Program.cs` / `ISessionRouter` wiring serialized.

## Context
`GameServer.Cluster.Redis` (room directory claim/release Lua, capacity placement) and the
out-of-process worker (`OutOfProcessWorker`, `RoomIpc`, `RoomWorkerHost`) are full seams but the host
uses `InMemorySessionRouter` and in-process rooms — `MultiNodeOwnershipScenario` shows split-brain is
currently possible. There is no node/room lifecycle state machine, allocation API, or headroom. See
gap analysis area 3.

## Scope
- Wire `RedisRoomDirectory`/placement behind `ISessionRouter` in a **clustered host** composition
  root (new project or a Host config flag — rank/declare as composition root in `repo-analyzers.json`).
- Room/node **lifecycle states** (Draining/Reserved/Allocated) beyond `Persist`/`Reap`.
- **Allocation API** (capacity-aware placement) + **reserved-capacity/headroom** model.
- **Per-node draining**: a draining node refuses new allocations and sheds/migrates cleanly.
- Optional: wire the out-of-process room host.
- Infra tests via Testcontainers Redis (or the existing fake).

## Tests required
- #5 `SingleOwnerAcrossNodes` (integration): a second node refuses to claim an already-owned room.
- `NodeDrain`: draining node stops accepting allocations; existing rooms migrate/shed.
- `CapacityHeadroom`: placement respects reserved headroom.

## Adversarial focus
Lease expiry / fencing races; split-brain under a simulated partition; drain correctness (no rooms
stranded or double-owned); allocation under contention.

## Acceptance criteria
Standard DoD + named tests green (Redis gated) + two nodes never co-own a room + a draining node
behaves correctly + clustered host ranked/declared.

## References
- Plan/spec: Appendix A §3; Test Gap Plan #5
- `GameServer.Cluster.Redis/*`, `Routing/*` (directory/affinity seams), `Transport/OutOfProcessWorker.cs`, `RoomIpc.cs`
