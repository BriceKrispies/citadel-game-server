using System.Threading.Channels;
using GameServer.Protocol;

namespace GameServer.Transport;

/// <summary>
/// In-memory full-duplex transport for in-process runs and tests. Behaves to the
/// same contract as a real WebSocket: the server side sends and receives; the
/// "client side" surface (<see cref="ClientSend"/> / <see cref="ReadOutboundAsync"/>)
/// lets a fake client drive the connection. Uses channels, never wall-clock time,
/// so it stays deterministic.
/// </summary>
public sealed class InMemoryBidirectionalTransport : IBidirectionalTransport
{
    private readonly Channel<MessageEnvelope> _inbound = Channel.CreateUnbounded<MessageEnvelope>();
    private readonly Channel<MessageEnvelope> _outbound = Channel.CreateUnbounded<MessageEnvelope>();

    public InMemoryBidirectionalTransport(ConnectionId connectionId) => ConnectionId = connectionId;

    public ConnectionId ConnectionId { get; }

    // ---- Server side (the realtime data plane) ------------------------------

    public Task SendAsync(MessageEnvelope message, CancellationToken cancellationToken = default)
    {
        if (!_outbound.Writer.TryWrite(message))
        {
            throw new InvalidOperationException("Outbound channel rejected a message.");
        }

        return Task.CompletedTask;
    }

    public async Task<MessageEnvelope?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        if (await _inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return _inbound.Reader.TryRead(out var message) ? message : null;
        }

        return null; // inbound completed: client closed the connection.
    }

    // ---- Client side (the fake client) --------------------------------------

    /// <summary>Enqueues a message as if sent by the client.</summary>
    public void ClientSend(MessageEnvelope message)
    {
        if (!_inbound.Writer.TryWrite(message))
        {
            throw new InvalidOperationException("Inbound channel rejected a message.");
        }
    }

    /// <summary>Signals the client closed the connection; the server receive loop will then complete.</summary>
    public void CompleteClient() => _inbound.Writer.TryComplete();

    /// <summary>Reads the next server-pushed message, or <c>null</c> if the server side completed.</summary>
    public async Task<MessageEnvelope?> ReadOutboundAsync(CancellationToken cancellationToken = default)
    {
        if (await _outbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return _outbound.Reader.TryRead(out var message) ? message : null;
        }

        return null;
    }

    /// <summary>Drains every server-pushed message currently buffered, in order.</summary>
    public IReadOnlyList<MessageEnvelope> DrainOutbound()
    {
        var drained = new List<MessageEnvelope>();
        while (_outbound.Reader.TryRead(out var message))
        {
            drained.Add(message);
        }

        return drained;
    }
}
