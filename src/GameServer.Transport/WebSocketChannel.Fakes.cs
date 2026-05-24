using System.Collections.Concurrent;
using System.Threading.Channels;

namespace GameServer.Transport;

/// <summary>
/// In-memory <see cref="IWebSocketChannel"/> for deterministic adapter tests.
/// Enqueue inbound frames, complete the inbound side to end the receive loop, and
/// inspect captured outbound frames. <see cref="Disconnect"/> models a dead socket:
/// receives end and sends throw, which the kernel treats as a dropped connection.
/// </summary>
public sealed class FakeWebSocketChannel : IWebSocketChannel
{
    private readonly Channel<string> _inbound = Channel.CreateUnbounded<string>();
    private readonly ConcurrentQueue<string> _sent = new();
    private volatile bool _disconnected;

    /// <summary>Frames sent by the server side, in order.</summary>
    public IReadOnlyList<string> Sent => _sent.ToArray();

    /// <summary>Enqueues a frame as if received from the client.</summary>
    public void EnqueueInbound(string text)
    {
        if (!_inbound.Writer.TryWrite(text))
        {
            throw new InvalidOperationException("Inbound channel rejected a frame.");
        }
    }

    /// <summary>Signals no more inbound frames so the server receive loop completes.</summary>
    public void CompleteInbound() => _inbound.Writer.TryComplete();

    /// <summary>Models an abrupt disconnect: receives end and subsequent sends fail.</summary>
    public void Disconnect()
    {
        _disconnected = true;
        _inbound.Writer.TryComplete();
    }

    public async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        if (_disconnected)
        {
            return null;
        }

        if (await _inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return _inbound.Reader.TryRead(out var text) ? text : null;
        }

        return null;
    }

    public Task SendTextAsync(string text, CancellationToken cancellationToken)
    {
        if (_disconnected)
        {
            throw new InvalidOperationException("Channel is disconnected.");
        }

        _sent.Enqueue(text);
        return Task.CompletedTask;
    }
}
