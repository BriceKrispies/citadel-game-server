# Citadel Realtime SDK (TypeScript)

A typed browser/Node client for the Citadel realtime protocol. It speaks the binary
WebSocket contract defined canonically by
`contracts/realtime/proto/gameserver.realtime.v1.proto` and pinned byte-for-byte by
the server-side golden fixtures (`contracts/realtime/fixtures/*.bin`).

## What's here

- `src/protocol.ts` — the protocol's enums and message interfaces, hand-mirrored from
  the `.proto` (same enum numeric values, same field names in camelCase, same oneof
  shape). Drop-in compatible with a `ts-proto`-generated model.
- `src/reconciler.ts` — `WorldView`: applies `ServerSnapshot` (full keyframe → replace),
  `ServerDelta` (incremental → merge changed + delete removed against the acked baseline),
  and `ServerCorrection` (authoritative override). This is the mirror image of the
  server's snapshot/delta contract.
- `src/client.ts` — `RealtimeClient`: drives hello → join → command/ack → leave over a
  binary WebSocket, keeping a reconciled `WorldView`, and acks only what it has applied
  (so the server's delta baseline advances only on confirmed receipt — never on send).

## Wire (de)serialization

`RealtimeClient` takes an `EnvelopeCodec` (`encode`/`decode` between `RealtimeEnvelope`
and protobuf bytes). The protocol/reconciler logic is codec-agnostic so it can be
unit-tested against a fake codec. For production, generate the binary-accurate codec
from the `.proto` (below) and adapt it to `EnvelopeCodec`.

## Regenerating from the `.proto`

The canonical types come from the `.proto`; regenerate them with `protoc` + `ts-proto`
whenever the contract changes (it is append-only — see
`contracts/realtime/COMPATIBILITY.md`):

```bash
# from sdk/typescript/
npm install                 # installs ts-proto + typescript (requires network + protoc on PATH)
npm run gen:proto           # → src/generated/gameserver/realtime/v1/*.ts (binary-accurate codec)
```

`protoc` must be on `PATH` (install from https://github.com/protocolbuffers/protobuf/releases
or `brew install protobuf` / `choco install protoc`). After generating, replace the
hand-written `protocol.ts` types with the generated module, or keep both and adapt the
generated `RealtimeEnvelope.encode/decode` to the `EnvelopeCodec` interface.

## Typechecking

```bash
npm install        # requires network
npm run typecheck  # tsc --noEmit
```

> Note: this environment had Node but neither `protoc` nor an offline TypeScript
> compiler, so the SDK ships hand-written types kept in lockstep with the `.proto`.
> The byte-level contract is enforced on the server by `GoldenPacketRoundTrip`; if the
> generated and hand-written models ever diverge, those fixtures fail first.
