using Xunit;

namespace GameServer.Protocol;

/// <summary>Round-trip tests that lock in protocol/wire stability independent of any transport.</summary>
public sealed class JsonMessageCodecTests
{
    private readonly JsonMessageCodec _codec = new();

    [Fact]
    public void ClientCommand_RoundTrips_PreservingEnvelopeAndPayload()
    {
        var original = new MessageEnvelope
        {
            TenantId = new TenantId("tenant-a"),
            GameId = new GameId("demo"),
            RoomId = new RoomId("arena"),
            SessionId = new SessionId("session-1"),
            PlayerId = new PlayerId("p1"),
            ProtocolVersion = ProtocolVersions.Current,
            MessageType = MessageType.ClientCommand,
            Sequence = 42,
            TraceId = "trace-xyz",
            Payload = new ClientCommand("MoveRight"),
        };

        var decoded = _codec.Decode(_codec.Encode(original));

        Assert.Equal(original, decoded);
        var command = Assert.IsType<ClientCommand>(decoded.Payload);
        Assert.Equal("MoveRight", command.Command);
    }

    [Fact]
    public void Hello_RoundTrips_WithNullIdentityFields()
    {
        var original = new MessageEnvelope
        {
            TenantId = new TenantId("tenant-a"),
            GameId = new GameId("demo"),
            ProtocolVersion = ProtocolVersions.Current,
            MessageType = MessageType.ClientHello,
            Sequence = 1,
            TraceId = "trace-hello",
            Payload = new ClientHello("fake-client"),
        };

        var decoded = _codec.Decode(_codec.Encode(original));

        Assert.Null(decoded.RoomId);
        Assert.Null(decoded.SessionId);
        Assert.Null(decoded.PlayerId);
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Decode_RejectsMalformedJson()
    {
        Assert.Throws<MessageCodecException>(() => _codec.Decode("{ not json"));
    }

    [Fact]
    public void Decode_RejectsUnknownMessageType()
    {
        const string wire = """
        {"tenantId":"t","gameId":"g","protocolVersion":1,"messageType":"NotARealType",
         "sequence":1,"traceId":"x","payload":{}}
        """;

        Assert.Throws<MessageCodecException>(() => _codec.Decode(wire));
    }

    [Fact]
    public void Decode_RejectsMissingRequiredField()
    {
        // No traceId field present.
        const string wire = """
        {"tenantId":"t","gameId":"g","protocolVersion":1,"messageType":"ClientHello",
         "sequence":1,"payload":{"clientName":"x"}}
        """;

        var ex = Assert.Throws<MessageCodecException>(() => _codec.Decode(wire));
        Assert.Contains("traceId", ex.Message); // the error names the offending field (operability)
    }

    [Fact]
    public void Decode_RejectsMissingRequiredNumberField()
    {
        // No protocolVersion (a numeric field): exercises the RequireNumber path, distinct from the
        // string-field path above. A missing number must surface as a codec error, not a raw NRE.
        const string wire = """
        {"tenantId":"t","gameId":"g","messageType":"ClientHello",
         "sequence":1,"traceId":"x","payload":{"clientName":"x"}}
        """;

        var ex = Assert.Throws<MessageCodecException>(() => _codec.Decode(wire));
        Assert.Contains("protocolVersion", ex.Message);
    }

    [Fact]
    public void Decode_RejectsWrongCaseMessageType()
    {
        // The message type is matched case-SENSITIVELY: "clienthello" is not "ClientHello". A
        // case-insensitive parse would silently accept a malformed type off the wire.
        const string wire = """
        {"tenantId":"t","gameId":"g","protocolVersion":1,"messageType":"clienthello",
         "sequence":1,"traceId":"x","payload":{"clientName":"x"}}
        """;

        Assert.Throws<MessageCodecException>(() => _codec.Decode(wire));
    }

    [Fact]
    public void Encode_RejectsInconsistentEnvelope()
    {
        // The declared MessageType must match the payload's type. Encoding a mismatch must fail
        // fast rather than emit a frame the peer cannot decode.
        var inconsistent = new MessageEnvelope
        {
            TenantId = new TenantId("t"),
            GameId = new GameId("g"),
            ProtocolVersion = ProtocolVersions.Current,
            MessageType = MessageType.ClientCommand,   // declares a command...
            Sequence = 1,
            TraceId = "x",
            Payload = new ClientHello("c"),             // ...but carries a hello
        };

        Assert.Throws<MessageCodecException>(() => _codec.Encode(inconsistent));
    }
}
