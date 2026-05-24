using GameServer.Protocol;

namespace GameServer.Transport;

/// <summary>
/// A push-only transport standing in for SSE/HTTP-push/spectator delivery. It
/// implements <see cref="IServerPushTransport"/> and deliberately does NOT
/// implement <see cref="IClientCommandReceiver"/>: such transports physically
/// cannot receive client commands, and the contracts let them say so honestly
/// instead of throwing from a method they should never have had.
/// </summary>
public sealed class BufferedServerPushTransport : IServerPushTransport
{
    private readonly List<MessageEnvelope> _pushed = new();

    public BufferedServerPushTransport(ConnectionId connectionId) => ConnectionId = connectionId;

    public ConnectionId ConnectionId { get; }

    /// <summary>Everything pushed to this client so far, in order.</summary>
    public IReadOnlyList<MessageEnvelope> Pushed => _pushed;

    public Task SendAsync(MessageEnvelope message, CancellationToken cancellationToken = default)
    {
        _pushed.Add(message);
        return Task.CompletedTask;
    }
}
