using System.Collections.Concurrent;
using System.Threading.Channels;

namespace GameServer.LoadHarness.Testing;

/// <summary>
/// In-memory <see cref="IWebSocketConnection"/> for deterministic virtual-client
/// tests. Capture sent frames, enqueue inbound frames, complete/close the inbound
/// side, and optionally gate receives to model a slow receiver — all without real
/// sockets or real time.
/// </summary>
public sealed class FakeWebSocketConnection : IWebSocketConnection
{
    private readonly Channel<ConnectionFrame> _inbound = Channel.CreateUnbounded<ConnectionFrame>();
    private readonly ConcurrentQueue<byte[]> _sent = new();
    private volatile TaskCompletionSource? _receiveGate;

    public IReadOnlyList<byte[]> Sent => _sent.ToArray();
    public bool Connected { get; private set; }
    public bool Closed { get; private set; }

    /// <summary>Blocks every subsequent receive until the returned source is completed.</summary>
    public TaskCompletionSource BlockReceive()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _receiveGate = gate;
        return gate;
    }

    public void EnqueueInbound(byte[] payload) =>
        _inbound.Writer.TryWrite(new ConnectionFrame(ConnectionFrameKind.Binary, payload));

    public void EnqueueClose() => _inbound.Writer.TryWrite(ConnectionFrame.Closed);

    public void CompleteInbound() => _inbound.Writer.TryComplete();

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        Connected = true;
        return Task.CompletedTask;
    }

    public Task SendBinaryAsync(byte[] payload, CancellationToken cancellationToken)
    {
        _sent.Enqueue(payload);
        return Task.CompletedTask;
    }

    public async Task<ConnectionFrame> ReceiveAsync(CancellationToken cancellationToken)
    {
        var gate = _receiveGate;
        if (gate is not null)
        {
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (await _inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return _inbound.Reader.TryRead(out var frame) ? frame : ConnectionFrame.Closed;
        }

        return ConnectionFrame.Closed;
    }

    public Task CloseAsync(CancellationToken cancellationToken)
    {
        Closed = true;
        _inbound.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
