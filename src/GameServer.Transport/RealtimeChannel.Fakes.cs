using System.Collections.Concurrent;
using System.Threading.Channels;
using GameServer.Protocol.Realtime.V1;

namespace GameServer.Transport;

/// <summary>
/// In-memory <see cref="IRealtimeChannel"/> for deterministic realtime-adapter tests.
/// Enqueue binary/text frames, complete the inbound side, and inspect captured
/// outbound binary frames and the close reason — no real WebSocket, no real time.
/// </summary>
public sealed class FakeRealtimeChannel : IRealtimeChannel
{
    private readonly Channel<RealtimeInboundFrame> _inbound = Channel.CreateUnbounded<RealtimeInboundFrame>();
    private readonly ConcurrentQueue<byte[]> _sent = new();

    public IReadOnlyList<byte[]> Sent => _sent.ToArray();
    public bool Closed { get; private set; }
    public DisconnectReason CloseReason { get; private set; }

    public void EnqueueBinary(byte[] payload) => _inbound.Writer.TryWrite(new RealtimeInboundFrame(RealtimeFrameKind.Binary, payload));

    public void EnqueueText(byte[] payload) => _inbound.Writer.TryWrite(new RealtimeInboundFrame(RealtimeFrameKind.Text, payload));

    public void CompleteInbound() => _inbound.Writer.TryComplete();

    public async Task<RealtimeInboundFrame> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (await _inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return _inbound.Reader.TryRead(out var frame) ? frame : RealtimeInboundFrame.Closed;
        }

        return RealtimeInboundFrame.Closed;
    }

    public Task SendBinaryAsync(byte[] payload, CancellationToken cancellationToken)
    {
        _sent.Enqueue(payload);
        return Task.CompletedTask;
    }

    public Task CloseAsync(DisconnectReason reason, string description, CancellationToken cancellationToken)
    {
        Closed = true;
        CloseReason = reason;
        _inbound.Writer.TryComplete();
        return Task.CompletedTask;
    }
}
