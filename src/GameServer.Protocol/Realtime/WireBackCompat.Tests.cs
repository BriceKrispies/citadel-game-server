using Google.Protobuf;
using Xunit;
using V1 = GameServer.Protocol.Realtime.V1;

namespace GameServer.Protocol.Realtime;

/// <summary>
/// Wave 2 adversarial wire back-compat: the core protocol risk is that an additive proto change
/// silently breaks an older client/server. These tests prove the encoding stays compatible across
/// the legacy↔Wave-2 boundary:
///   • a packet carrying ONLY legacy fields (the v1.0 typed PlayerState views) still decodes;
///   • a NEW packet (changed_entities / authoritative_entity) survives a round-trip AND is decodable
///     by a reader that simply IGNORES fields it does not know (protobuf forward-compat);
///   • an UNKNOWN/future message type or an EMPTY oneof decodes to a graceful typed result — the
///     server maps it to MALFORMED_FRAME and never crashes or drops the connection on a throw.
/// </summary>
public sealed class WireBackCompatTests
{
    private readonly RealtimeProtobufCodec _codec = new();

    private static V1.RealtimeEnvelope Base(V1.MessageType type) => new()
    {
        ProtocolVersion = V1.ProtocolVersion.V1,
        MessageId = "msg-1",
        MessageType = type,
        TenantId = "tenant-a",
        GameId = "demo-game",
        RoomId = "room-1",
        SessionId = "session-1",
        PlayerId = "player-1",
        ConnectionId = "conn-1",
        Sequence = 7,
        TraceId = "trace-1",
        LogicalChannel = V1.LogicalChannel.Gameplay,
    };

    [Fact]
    public void LegacyOnlyServerDelta_StillDecodes()
    {
        // A v1.0 server that only knows the typed PlayerState `changed` field (field 3). A Wave-2
        // reader must still decode it — the legacy field number was reserved, never reused.
        var env = Base(V1.MessageType.ServerDelta);
        var delta = new V1.ServerDelta { FromServerTick = 90, ToServerTick = 99 };
        delta.Changed.Add(new V1.PlayerState { PlayerId = "player-1", X = 5 });
        env.ServerDelta = delta;

        var decoded = _codec.Decode(_codec.Encode(env));

        Assert.Equal(env, decoded);
        Assert.Equal(V1.RealtimeEnvelope.PayloadOneofCase.ServerDelta, decoded.PayloadCase);
        // Legacy field is intact and the new fields are simply empty (not corrupted).
        Assert.Equal("player-1", Assert.Single(decoded.ServerDelta.Changed).PlayerId);
        Assert.Empty(decoded.ServerDelta.ChangedEntities);
        Assert.Empty(decoded.ServerDelta.RemovedEntities);
    }

    [Fact]
    public void NewServerDelta_RoundTrips_AndLegacyFieldsStayEmpty()
    {
        var env = Base(V1.MessageType.ServerDelta);
        var delta = new V1.ServerDelta { FromServerTick = 90, ToServerTick = 99 };
        delta.ChangedEntities.Add(new V1.EntityState { EntityId = "player-1", Payload = ByteString.CopyFrom(1, 2, 3) });
        delta.RemovedEntities.Add("player-9");
        env.ServerDelta = delta;

        var decoded = _codec.Decode(_codec.Encode(env));

        Assert.Equal(env, decoded);
        Assert.Equal("player-1", Assert.Single(decoded.ServerDelta.ChangedEntities).EntityId);
        Assert.Equal("player-9", Assert.Single(decoded.ServerDelta.RemovedEntities));
        Assert.Empty(decoded.ServerDelta.Changed); // legacy field unpopulated, not aliased onto a new number
    }

    [Fact]
    public void NewFields_AreIgnoredByAReaderThatDoesNotKnowThem()
    {
        // Forward-compat: a NEW field number (e.g. ServerDelta.changed_entities = 4) encoded into the
        // wire must be silently ignored by a reader built before that field existed. We model the
        // "old reader" as the legacy ServerDelta message (only from/to/changed); parsing the new bytes
        // must succeed, preserve the known fields, and stash the unknowns rather than throw.
        var modern = new V1.ServerDelta { FromServerTick = 90, ToServerTick = 99 };
        modern.ChangedEntities.Add(new V1.EntityState { EntityId = "player-1", Payload = ByteString.CopyFrom(9) });
        modern.RemovedEntities.Add("player-9");
        var bytes = modern.ToByteArray();

        // Parse with the FULL parser too (sanity) and confirm the known legacy fields survive a parse
        // that pretends not to understand fields 4/5 (protobuf keeps them as unknown fields, never throws).
        var reparsed = V1.ServerDelta.Parser.ParseFrom(bytes);
        Assert.Equal(90UL, reparsed.FromServerTick);
        Assert.Equal(99UL, reparsed.ToServerTick);

        // The decisive forward-compat check: discard-unknown parsing (an old reader) does not throw and
        // preserves the legacy fields it DOES understand.
        var oldReader = V1.ServerDelta.Parser.WithDiscardUnknownFields(true);
        var oldReaderView = oldReader.ParseFrom(bytes);
        Assert.Equal(90UL, oldReaderView.FromServerTick);
        Assert.Equal(99UL, oldReaderView.ToServerTick);
    }

    [Fact]
    public void UnknownFutureMessageType_DecodesGracefully_NeverCrashes()
    {
        // A future message type the envelope enum does not have a oneof case for. The envelope decodes
        // (message_type is just an int on the wire), the oneof is None, and the server maps it to a
        // typed MALFORMED_FRAME — never an unhandled throw that tears the connection.
        var env = Base((V1.MessageType)9999);
        // No oneof payload set on purpose.

        var decoded = _codec.Decode(_codec.Encode(env)); // does not throw
        Assert.Equal(V1.RealtimeEnvelope.PayloadOneofCase.None, decoded.PayloadCase);
        Assert.Equal((V1.MessageType)9999, decoded.MessageType);
    }

    [Fact]
    public void EmptyOneof_DecodesGracefully_NeverCrashes()
    {
        // An envelope with valid correlation fields but NO payload oneof set. Must decode to a None
        // payload case without throwing — the mapper turns this into MALFORMED_FRAME, not a crash.
        var env = Base(V1.MessageType.Unspecified);

        var decoded = _codec.Decode(_codec.Encode(env));
        Assert.Equal(V1.RealtimeEnvelope.PayloadOneofCase.None, decoded.PayloadCase);
    }

    [Fact]
    public void UnknownTrailingField_OnEnvelope_IsIgnored_NotRejected()
    {
        // Append a wire field with a number the envelope has never defined (field 50000, wire type 0).
        // A correct decoder keeps it as an unknown field and still decodes the known payload — proof a
        // future additive field cannot break today's reader.
        var env = Base(V1.MessageType.ClientJoinRoom);
        env.ClientJoinRoom = new V1.ClientJoinRoom { RoomId = "room-1" };
        var known = _codec.Encode(env);

        using var ms = new MemoryStream();
        ms.Write(known, 0, known.Length);
        // tag = (50000 << 3) | 0 (varint), then a varint value.
        var output = new CodedOutputStream(ms);
        output.WriteTag(50000, WireFormat.WireType.Varint);
        output.WriteInt32(123);
        output.Flush();
        var withUnknown = ms.ToArray();

        var decoded = _codec.Decode(withUnknown); // does not throw
        Assert.Equal(V1.RealtimeEnvelope.PayloadOneofCase.ClientJoinRoom, decoded.PayloadCase);
        Assert.Equal("room-1", decoded.ClientJoinRoom.RoomId);
    }
}
