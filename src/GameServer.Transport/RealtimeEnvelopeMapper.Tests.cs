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
}
