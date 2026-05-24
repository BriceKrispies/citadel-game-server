namespace GameServer.Protocol;

/// <summary>
/// Encodes and decodes protocol envelopes to/from a wire string. Defined as a
/// seam so the transport layer never bakes in a serialization format, and so the
/// protocol can be round-trip tested independently of any transport.
/// </summary>
public interface IMessageCodec
{
    string Encode(MessageEnvelope envelope);

    MessageEnvelope Decode(string wire);
}

/// <summary>Thrown when a wire payload cannot be decoded into a valid envelope.</summary>
public sealed class MessageCodecException : Exception
{
    public MessageCodecException(string message) : base(message) { }
}
