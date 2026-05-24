using GameServer.Protocol;

namespace GameServer.Transport;

/// <summary>
/// A transport that can push server-originated messages to a client. This is the
/// only capability SSE/HTTP-push/spectator transports can honestly provide, so it
/// stands alone. Implementations must not be required to receive client input.
/// </summary>
public interface IServerPushTransport
{
    ConnectionId ConnectionId { get; }

    /// <summary>Pushes one server message to the client.</summary>
    Task SendAsync(MessageEnvelope message, CancellationToken cancellationToken = default);
}

/// <summary>
/// A transport that can receive client-originated commands. Defined separately so
/// a push-only transport is never forced to implement (and lie about) receiving.
/// </summary>
public interface IClientCommandReceiver
{
    ConnectionId ConnectionId { get; }

    /// <summary>
    /// Returns the next inbound message, or <c>null</c> once the client side is
    /// closed/complete. Never blocks on wall-clock time in the in-memory transport.
    /// </summary>
    Task<MessageEnvelope?> ReceiveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Full-duplex transport (e.g. WebSocket). Composed from the two single-capability
/// contracts; it adds no members of its own, so substituting a push-only transport
/// where only <see cref="IServerPushTransport"/> is required is always valid.
/// </summary>
public interface IBidirectionalTransport : IServerPushTransport, IClientCommandReceiver
{
    /// <summary>Resolves the identical <c>ConnectionId</c> inherited from both base contracts.</summary>
    new ConnectionId ConnectionId { get; }
}

/// <summary>
/// Optional capability for a transport that handles some inbound frames at the edge — pings it
/// answers itself, frames it drops — without ever returning them from <see cref="IClientCommandReceiver.ReceiveAsync"/>.
/// Such frames are real client activity, but the server's receive loop never sees them, so a client
/// that sends only those would look idle and be reaped as a zombie. A transport implements this to
/// expose a monotonic count of frames it has read from the wire (any kind); the server treats an
/// advancing count as proof of life, distinguishing a genuinely silent socket (count never moves →
/// reap) from a busy one whose traffic the transport consumes at the edge (count moves → keep alive).
/// Transports whose <c>ReceiveAsync</c> returns once per inbound frame need not implement this.
/// </summary>
public interface IInboundActivityProbe
{
    /// <summary>Monotonic count of frames read from the wire over the connection's life (any kind).</summary>
    long InboundFrameCount { get; }
}
