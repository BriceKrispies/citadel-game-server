# Citadel

An enterprise-grade, multi-tenant multiplayer game server backend in .NET 10. The
design goal is a small, deterministic, observable **authoritative server kernel**
surrounded by replaceable infrastructure. Game behavior is provable without any
infrastructure (no AWS, Redis, database, sockets, or wall-clock time in the core).

## Purpose

- Authoritative server-side simulation (the server owns truth; clients send intent).
- Multi-tenant isolation, enforced structurally rather than with conditionals.
- Realtime data plane kept boring, fast, observable, and testable.
- Test-driven: behavior is proven in-process before any production infrastructure exists.

## API surface (platform contract)

The public surface is split into a control plane (HTTP/JSON) and a realtime data
plane (WebSocket/binary protobuf). Contracts live under `contracts/`.

**HTTP control plane** (session lifecycle only — never gameplay):

| Method | Path |
| --- | --- |
| GET | `/health`, `/ready`, `/version` |
| GET | `/api/v1/games`, `/api/v1/games/{gameId}` |
| POST / GET | `/api/v1/rooms`, `/api/v1/rooms/{roomId}` |
| POST | `/api/v1/rooms/{roomId}/join-token` |
| POST / DELETE | `/api/v1/sessions`, `/api/v1/sessions/{sessionId}` |

All HTTP responses are stable DTOs; errors are `{ "code", "message" }`
(see `contracts/realtime/ERRORS.md`). Full surface: `contracts/http/openapi.md`.

**Realtime WebSocket endpoint** (primary gameplay transport):

```
GET /realtime/v1/connect?joinToken=<token>
```

**Binary protocol statement:** realtime frames are **binary protobuf** only
(`RealtimeEnvelope`, canonical schema in
`contracts/realtime/proto/gameserver.realtime.v1.proto`). Text frames are rejected
with a typed `ServerError`. JSON exists solely as a dev/debug codec on the
development-only `/ws` path and is never the contract. No SignalR. See
`contracts/realtime/PROTOCOL.md` and `COMPATIBILITY.md`.

## Current vertical slice

The first slice proves this full path **entirely in-process, with no real time**:

```
fake client connects
  → tenant resolves
  → protocol version negotiates
  → session is created (ServerWelcome)
  → player joins one room
  → client sends one MoveRight command
  → command is routed to the room
  → room applies it on a deterministic tick (X += 1)
  → authoritative state changes
  → server emits a ServerSnapshot
  → fake client receives the snapshot
```

The game rule is intentionally trivial: a player has an integer `X`; `MoveRight`
increments it by one on the next tick.

## Project layout

| Project | Responsibility | Depends on |
| --- | --- | --- |
| `GameServer.Protocol` | Versioned envelope, typed messages, identifiers, codec | — (pure) |
| `GameServer.Tenancy` | `TenantContext`, `ITenantResolver` + in-memory resolver | Protocol |
| `GameServer.Observability` | `ITelemetrySink`, metric/event names, null sink | — |
| `GameServer.Persistence` | Generic `ISnapshotStore`/`IEventLog` + in-memory stores | — (generic) |
| `GameServer.Simulation` | `IGameRoom`, `ISimulationClock`, `IRandomSource`, room model | Protocol only |
| `GameServer.Routing` | `ISessionRouter`, room placement, protocol↔sim mapping | Protocol, Tenancy, Simulation, Persistence, Observability |
| `GameServer.Transport` | Transport contracts, in-memory transport, `RealtimeServer` | the edge (refs inward) |
| `GameServer.ControlPlane` | Placeholder (tenant registry, provisioning, policy) | Tenancy, Protocol |
| `GameServer.Admin` | Placeholder (admin/operational APIs) | ControlPlane |
| `GameServer.LoadHarness` | Placeholder (overload/backpressure driver) | Transport |
| `GameServer.Tests` | xUnit test assembly; compiles the co-located tests + Testing/ support | all |
| `tools/RepoAnalyzers/Citadel.RepoAnalyzers` | Roslyn analyzer enforcing co-located tests (CITADEL0001) | — |

Dependencies flow inward. The simulation depends only on the pure `Protocol`
identifiers and nothing else.

## Test layout (co-located)

Tests live **physically beside the code they test** but compile into a separate
test assembly:

```
src/GameServer.Simulation/
  GameRoom.cs            ← production (compiles into GameServer.Simulation.dll)
  GameRoom.Tests.cs      ← unit test  (compiles into GameServer.Tests.dll)
  Testing/               ← cross-feature test support (test assembly only)
    FakeSimulationClock.cs
```

Production projects exclude `**/*.Tests.cs`, `**/*.Test.cs`, `**/*.Fakes.cs`,
`**/*.TestData.cs`, `**/*.TestDoubles.cs`, and `**/Testing/**/*.cs`
(`src/Directory.Build.targets`); the test project includes them by glob. The
`CITADEL0001` analyzer flags any production `.cs` missing a co-located test;
ignored categories live in `repo-analyzers.json`.

## Validation commands

| Goal | Command |
| --- | --- |
| Fast unit tests | `dotnet test tests/GameServer.Tests/GameServer.Tests.csproj` |
| Analyzer tests | `dotnet test tools/RepoAnalyzers/Citadel.RepoAnalyzers.Tests/Citadel.RepoAnalyzers.Tests.csproj` |
| Full solution build (+ runs the analyzer over the repo) | `dotnet build Citadel.slnx` |
| Everything (all test projects) | `dotnet test Citadel.slnx` |
| Mutation testing (deep validation) | `dotnet tool restore` then `dotnet stryker --project GameServer.Simulation.csproj` |
| Local browser multiplayer demo | `dotnet run --project src/GameServer.Host` then open `http://localhost:5000` (see [DEMO.md](DEMO.md)) |
| Local load smoke (binary protobuf) | start the host, then `dotnet run --project src/GameServer.LoadHarness -- load/scenarios/local-smoke.json` (see [LOAD_TESTING.md](LOAD_TESTING.md)) |

The full build treats warnings as errors, except `CITADEL0001` (missing co-located
test), which is intentionally a non-fatal warning so the convention can be adopted
incrementally. Mutation testing is **not** part of the fast loop — run it per
production project as deeper validation. A run writes an HTML report under
`StrykerOutput/`.

## The non-negotiable rule

**The simulation layer must remain infrastructure-free.** `GameServer.Simulation`
must never depend on ASP.NET Core, WebSockets, HTTP, AWS, Redis, databases, or
wall-clock time. All time comes from an injected `ISimulationClock`; all
randomness from an injected `IRandomSource`. If a change would make Simulation
depend on infrastructure, stop and introduce a better abstraction instead.
