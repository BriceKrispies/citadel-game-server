using GameServer.Protocol.Realtime.V1;
using Xunit;
using K = GameServer.Protocol;

namespace GameServer.Transport;

/// <summary>
/// Pins the kernel-to-wire error-code mapping. The security-decisive invariant is that an
/// authorization rejection surfaces as <c>UNAUTHORIZED</c> on the wire (not a misleading
/// <c>INTERNAL_SERVER_ERROR</c>), and that load-shedding surfaces as <c>BACKPRESSURE_REJECTED</c>.
/// (Closes Finding 5 from the black-box pentest report.)
/// </summary>
public sealed class RealtimeEnvelopeMapperTests
{
    private static readonly RealtimeEnvelopeMapper Mapper = new();

    private static ServerError MapKernelError(K.ServerErrorCode code)
    {
        var kernel = new K.MessageEnvelope
        {
            TenantId = new K.TenantId("tenant-a"),
            GameId = new K.GameId("grid-walk"),
            RoomId = new K.RoomId("arena"),
            PlayerId = new K.PlayerId("p1"),
            ProtocolVersion = K.ProtocolVersions.Current,
            MessageType = K.MessageType.ServerError,
            Sequence = 1,
            TraceId = "trace-1",
            Payload = new K.ServerError(code, "denied"),
        };

        var wire = Mapper.MapServer(kernel);
        Assert.Equal(MessageType.ServerError, wire.MessageType);
        return wire.ServerError;
    }

    [Fact]
    public void Unauthorized_MapsToUnauthorized_NotInternalServerError()
    {
        Assert.Equal(ErrorCode.Unauthorized, MapKernelError(K.ServerErrorCode.Unauthorized).Code);
    }

    [Fact]
    public void Overloaded_MapsToBackpressureRejected_NotInternalServerError()
    {
        Assert.Equal(ErrorCode.BackpressureRejected, MapKernelError(K.ServerErrorCode.Overloaded).Code);
    }

    [Theory]
    [InlineData(K.ServerErrorCode.UnknownTenant, ErrorCode.TenantNotFound)]
    [InlineData(K.ServerErrorCode.UnsupportedProtocolVersion, ErrorCode.UnsupportedProtocolVersion)]
    [InlineData(K.ServerErrorCode.InvalidCommand, ErrorCode.MalformedFrame)]
    [InlineData(K.ServerErrorCode.StaleSequence, ErrorCode.SequenceRejected)]
    [InlineData(K.ServerErrorCode.NotJoined, ErrorCode.PlayerNotInRoom)]
    [InlineData(K.ServerErrorCode.RoomUnavailable, ErrorCode.RoomNotFound)]
    [InlineData(K.ServerErrorCode.MalformedMessage, ErrorCode.MalformedFrame)]
    public void KnownKernelCodes_MapToTheirWireCode(K.ServerErrorCode kernel, ErrorCode expected)
    {
        Assert.Equal(expected, MapKernelError(kernel).Code);
    }

    private static K.MessageEnvelope KernelEnvelope(K.MessageType type, K.IMessagePayload payload) => new()
    {
        TenantId = new K.TenantId("tenant-a"),
        GameId = new K.GameId("grid-walk"),
        RoomId = new K.RoomId("arena"),
        PlayerId = new K.PlayerId("p1"),
        ProtocolVersion = K.ProtocolVersions.Current,
        MessageType = type,
        Sequence = 1,
        TraceId = "trace-1",
        Payload = payload,
    };

    [Fact]
    public void MapServer_ServerDelta_CarriesChangedAndRemovedEntities()
    {
        var kernel = KernelEnvelope(K.MessageType.ServerDelta, new K.ServerDelta(
            FromTick: 5, ToTick: 9,
            Changed: new[] { new K.EntityState("p1", new byte[] { 1, 2 }) },
            Removed: new[] { "p9" }));

        var wire = Mapper.MapServer(kernel);

        Assert.Equal(MessageType.ServerDelta, wire.MessageType);
        Assert.Equal(5UL, wire.ServerDelta.FromServerTick);
        Assert.Equal(9UL, wire.ServerDelta.ToServerTick);
        Assert.Equal("p1", Assert.Single(wire.ServerDelta.ChangedEntities).EntityId);
        Assert.Equal("p9", Assert.Single(wire.ServerDelta.RemovedEntities));
    }

    [Fact]
    public void MapServer_ServerCorrection_CarriesAuthoritativeEntity()
    {
        var kernel = KernelEnvelope(K.MessageType.ServerCorrection, new K.ServerCorrection(
            Tick: 12, Authoritative: new K.EntityState("p1", new byte[] { 7 }), AckedClientTick: 4));

        var wire = Mapper.MapServer(kernel);

        Assert.Equal(MessageType.ServerCorrection, wire.MessageType);
        Assert.Equal(12UL, wire.ServerCorrection.ServerTick);
        Assert.Equal(4UL, wire.ServerCorrection.AckedClientTick);
        Assert.Equal("p1", wire.ServerCorrection.AuthoritativeEntity.EntityId);
    }

    [Fact]
    public void MapServer_ServerEvent_CarriesTypeAndPayload()
    {
        var kernel = KernelEnvelope(K.MessageType.ServerEvent,
            new K.ServerEvent(K.ServerEvent.Types.PlayerJoined, System.Text.Encoding.UTF8.GetBytes("p1")));

        var wire = Mapper.MapServer(kernel);

        Assert.Equal(MessageType.ServerEvent, wire.MessageType);
        Assert.Equal("player_joined", wire.ServerEvent.EventType);
        Assert.Equal("p1", wire.ServerEvent.Payload.ToStringUtf8());
    }

    [Fact]
    public void MapClient_ClientLeaveRoom_ForwardsToKernel()
    {
        var env = new RealtimeEnvelope
        {
            ProtocolVersion = ProtocolVersion.V1,
            MessageType = MessageType.ClientLeaveRoom,
            TenantId = "tenant-a",
            GameId = "grid-walk",
            RoomId = "arena",
            PlayerId = "p1",
            TraceId = "trace-1",
            ClientLeaveRoom = new ClientLeaveRoom { RoomId = "arena" },
        };

        var mapped = Mapper.MapClient(env);

        Assert.Equal(RealtimeClientDispatch.ForwardToKernel, mapped.Dispatch);
        Assert.Equal(K.MessageType.ClientLeaveRoom, mapped.Kernel!.MessageType);
        Assert.IsType<K.ClientLeaveRoom>(mapped.Kernel.Payload);
    }

    [Fact]
    public void MapClient_UnknownPayload_IsHandledGracefully_AsMalformedFrameError_NotACrash()
    {
        // An envelope with no payload set (unknown/unsupported message): the mapper must answer
        // with a typed ServerError, never throw — a new/unknown wire message cannot crash the edge.
        var env = new RealtimeEnvelope
        {
            ProtocolVersion = ProtocolVersion.V1,
            MessageType = MessageType.Unspecified,
            TenantId = "tenant-a",
            GameId = "grid-walk",
            TraceId = "trace-1",
        };

        var mapped = Mapper.MapClient(env);

        Assert.Equal(RealtimeClientDispatch.RespondImmediately, mapped.Dispatch);
        Assert.Equal(MessageType.ServerError, mapped.Response!.MessageType);
        Assert.Equal(ErrorCode.MalformedFrame, mapped.Response.ServerError.Code);
    }
}
