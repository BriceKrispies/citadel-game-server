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
