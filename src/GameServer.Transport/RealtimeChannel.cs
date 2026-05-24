using GameServer.Protocol.Realtime.V1;

namespace GameServer.Transport;

/// <summary>The kind of WebSocket frame received. The realtime protocol is binary-only.</summary>
public enum RealtimeFrameKind
{
    Binary,
    Text,
    Closed,
}

/// <summary>An inbound WebSocket frame: its kind plus payload bytes (empty for text/close as needed).</summary>
public readonly record struct RealtimeInboundFrame(RealtimeFrameKind Kind, byte[] Payload)
{
    public static readonly RealtimeInboundFrame Closed = new(RealtimeFrameKind.Closed, Array.Empty<byte>());
}

/// <summary>
/// A binary-framed WebSocket channel. The seam that keeps the protobuf realtime
/// adapter testable without a real socket: production wraps an ASP.NET WebSocket;
/// tests use an in-memory fake. The channel surfaces frame kind so the adapter can
/// reject text frames per protocol.
/// </summary>
public interface IRealtimeChannel
{
    Task<RealtimeInboundFrame> ReceiveAsync(CancellationToken cancellationToken);

    Task SendBinaryAsync(byte[] payload, CancellationToken cancellationToken);

    /// <summary>Closes the connection with a stable, machine-readable reason.</summary>
    Task CloseAsync(DisconnectReason reason, string description, CancellationToken cancellationToken);
}
