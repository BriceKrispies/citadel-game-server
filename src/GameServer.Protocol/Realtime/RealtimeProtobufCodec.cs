using GameServer.Protocol.Realtime.V1;
using Google.Protobuf;

namespace GameServer.Protocol.Realtime;

/// <summary>Thrown when a realtime binary frame cannot be decoded or violates protocol policy.</summary>
public sealed class RealtimeProtocolException : Exception
{
    public RealtimeProtocolException(ErrorCode code, string message) : base(message) => Code = code;

    /// <summary>The stable, client-facing error code for this failure.</summary>
    public ErrorCode Code { get; }
}

/// <summary>
/// The canonical binary codec for the realtime protocol: protobuf-encoded
/// <see cref="RealtimeEnvelope"/> frames. Decoding rejects malformed bytes and
/// unsupported protocol versions with typed <see cref="RealtimeProtocolException"/>s.
/// </summary>
public sealed class RealtimeProtobufCodec
{
    public byte[] Encode(RealtimeEnvelope envelope) => envelope.ToByteArray();

    public RealtimeEnvelope Decode(byte[] frame)
    {
        RealtimeEnvelope envelope;
        try
        {
            envelope = RealtimeEnvelope.Parser.ParseFrom(frame);
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new RealtimeProtocolException(ErrorCode.MalformedFrame, $"Malformed binary frame: {ex.Message}");
        }

        if (!RealtimeProtocol.IsSupported(envelope.ProtocolVersion))
        {
            throw new RealtimeProtocolException(
                ErrorCode.UnsupportedProtocolVersion,
                $"Protocol version '{envelope.ProtocolVersion}' is not supported.");
        }

        return envelope;
    }
}
