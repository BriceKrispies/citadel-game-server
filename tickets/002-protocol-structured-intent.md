# 002 — Protocol: append-only structured intent payload on ClientCommand

**Status:** open
**Area:** Protocol / Transport / SDK
**Depends on:** 001 (go decision)

## Context
`ClientCommand` carries only `string command` today — there is almost nothing to schema-validate. To enforce
a declared contract, commands need a structured payload. Append-only so old clients are unaffected.

## Scope
- Proto: add `bytes intent = 2;` to `ClientCommand` (keep `command = 1`; never renumber). Regenerate.
- Kernel record `ClientCommand(string Command, ReadOnlyMemory<byte> Intent = default)` (stays immutable);
  carry `Intent` through `RealtimeEnvelopeMapper` (both directions) + `JsonMessageCodec` (base64).
- TypeScript SDK: `intent?: Uint8Array` on `ClientCommand` + an `encodeIntent(contract, obj)` helper.
- Golden fixtures: add a `command.bin` exercising both `command` + `intent`; extend `GoldenPacketRoundTrip`
  and the protobuf codec round-trip test. Back-compat: old clients send only field 1 -> empty `Intent`.

## Tests / acceptance
- Round-trip (binary + JSON) preserves `Intent`; legacy-only packet still decodes; golden fixtures pinned.
- Build clean; fast + integration green; SecurityTests still skip-green.

## References
- Approved plan: `~/.claude/plans/i-want-a-full-piped-candle.md` (Phase 1).
- `contracts/realtime/proto/gameserver.realtime.v1.proto`, `src/GameServer.Protocol/Payloads.cs`, `RealtimeEnvelopeMapper.cs`, `sdk/typescript/src/protocol.ts`.
