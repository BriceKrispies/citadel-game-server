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
            Payload = new ClientCommand(ClientCommandType.MoveRight),
        };

        var decoded = _codec.Decode(_codec.Encode(original));

        Assert.Equal(original, decoded);
        var command = Assert.IsType<ClientCommand>(decoded.Payload);
        Assert.Equal(ClientCommandType.MoveRight, command.Command);
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

        Assert.Throws<MessageCodecException>(() => _codec.Decode(wire));
    }
}
