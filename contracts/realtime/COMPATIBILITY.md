# Realtime Protocol Compatibility Rules

The realtime protobuf schema is a **stable platform contract**. Browser clients are
built against it and must keep working as the server evolves.

## Rules

1. **Field numbers are never reused or renumbered.** Once a field number is assigned
   in `gameserver.realtime.v1.proto`, it is permanent. Removing a field reserves its
   number (`reserved`); it is never reassigned to a different field.

2. **Additive changes are preferred.** New optional fields, new `oneof` cases, and new
   enum values are backward-compatible and are the default way to evolve the protocol.
   Proto3 ignores unknown fields, so older servers/clients tolerate new fields.

3. **Old clients receive stable errors for unsupported versions.** When a client
   requests a `protocol_version` the server does not support, the server returns a
   `ServerError` with `UNSUPPORTED_PROTOCOL_VERSION` and closes with
   `DISCONNECT_REASON_UNSUPPORTED_VERSION`. It never silently degrades.

4. **Breaking changes require a new major protocol version.** Renaming/retyping a
   field, changing field semantics, or removing a `oneof` case is breaking. Such
   changes ship as a new package (`gameserver.realtime.v2`) and a new
   `ProtocolVersion` value; the server may support multiple versions concurrently.

5. **Unknown game payloads must fail explicitly.** A `GameMessage` whose
   `game_message_type` or `game_schema_version` is not registered fails with
   `UNKNOWN_GAME_MESSAGE`. The platform never guesses or silently drops game payloads.

6. **Protocol version must be visible in telemetry.** Every connection records its
   negotiated `protocol_version` (and message-level versions where relevant) so
   version adoption and incompatibilities are observable in dashboards.

## Enum evolution

- Every enum has an explicit `*_UNSPECIFIED = 0` zero value; `0` is never a real
  meaning. Receivers treat unknown enum values defensively (do not crash).
- New enum values are additive; consumers must tolerate values they do not recognize.

## Versioning summary

| Change | Compatible? | How |
| --- | --- | --- |
| Add optional field | Yes | New field number |
| Add `oneof` case | Yes | New field number in the oneof |
| Add enum value | Yes | Append; never reuse numbers |
| Remove field | Reserve | `reserved` the number + name |
| Rename/retype field | No | New major version (`vN+1`) |
| Change field meaning | No | New major version (`vN+1`) |
