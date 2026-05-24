using GameServer.Protocol;

namespace GameServer.Transport;

/// <summary>What happened when fan-out tried to hand a message to a connection's outbound buffer.</summary>
public enum OutboundEnqueueResult
{
    /// <summary>Accepted: sent inline (the socket was ready) or buffered for the background writer.</summary>
    Enqueued,

    /// <summary>The buffer was full (a slow/stalled client); the message was dropped, not awaited.</summary>
    DroppedQueueFull,

    /// <summary>The connection is closing/closed (or a send faulted); nothing was buffered.</summary>
    DroppedClosed,
}

/// <summary>
/// A per-connection, bounded, non-blocking outbound buffer that decouples the room tick from
/// socket I/O. Fan-out hands each snapshot to <see cref="TryEnqueue"/>, which never blocks on the
/// socket: if the transport can accept the write immediately it is sent inline (the common case —
/// preserving in-order, observable delivery), but if the write would block (a slow/stalled client)
/// the message is buffered and a background writer drains it, so one slow client can never freeze
/// the authoritative tick (head-of-line blocking) nor grow server memory without bound.
/// </summary>
public interface IOutboundChannel : IAsyncDisposable
{
    /// <summary>Messages currently buffered behind an in-flight send and not yet flushed.</summary>
    int QueueDepth { get; }

    /// <summary>Messages dropped so far because the buffer was full (a backpressure signal).</summary>
    long DroppedCount { get; }

    /// <summary>
    /// Buffers (or inline-sends) a message for delivery. Never blocks on socket I/O and never
    /// throws on a slow client — a full buffer yields <see cref="OutboundEnqueueResult.DroppedQueueFull"/>.
    /// </summary>
    OutboundEnqueueResult TryEnqueue(MessageEnvelope message);
}

/// <summary>
/// Bounded outbound buffer over an <see cref="IServerPushTransport"/>. While no send is in flight
/// it writes inline (so a ready socket delivers synchronously and in order); once a send is
/// pending it buffers up to <c>capacity</c> behind a single background drain and rejects the
/// newest beyond that, so a slow reader can never grow server memory without bound.
/// </summary>
public sealed class BoundedOutboundChannel : IOutboundChannel
{
    private readonly IServerPushTransport _transport;
    private readonly int _capacity;
    private readonly object _lock = new();
    private readonly Queue<MessageEnvelope> _buffer = new();
    private bool _draining;
    private bool _closed;
    private long _dropped;

    public BoundedOutboundChannel(IServerPushTransport transport, int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Outbound capacity must be positive.");
        }

        _transport = transport;
        _capacity = capacity;
    }

    public int QueueDepth
    {
        get { lock (_lock) { return _buffer.Count; } }
    }

    public long DroppedCount
    {
        get { lock (_lock) { return _dropped; } }
    }

    public OutboundEnqueueResult TryEnqueue(MessageEnvelope message)
    {
        lock (_lock)
        {
            if (_closed)
            {
                return OutboundEnqueueResult.DroppedClosed;
            }

            if (!_draining)
            {
                // Nothing in flight: try to send right now. A ready socket (the common
                // in-memory/fast case) completes synchronously, so delivery stays inline and
                // ordered. A write that would block falls through to buffering without awaiting.
                var send = SafeSend(message);
                if (send.IsCompletedSuccessfully)
                {
                    return OutboundEnqueueResult.Enqueued;
                }

                if (send.IsCompleted)
                {
                    // Faulted or canceled synchronously: the connection is no longer writable.
                    _closed = true;
                    return OutboundEnqueueResult.DroppedClosed;
                }

                // The write is pending (a slow client): drain the rest behind it, off the tick.
                _draining = true;
                _ = DrainAfterAsync(send);
                return OutboundEnqueueResult.Enqueued;
            }

            if (_buffer.Count >= _capacity)
            {
                _dropped++;
                return OutboundEnqueueResult.DroppedQueueFull;
            }

            _buffer.Enqueue(message);
            return OutboundEnqueueResult.Enqueued;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            _closed = true;
            _buffer.Clear();
        }

        return ValueTask.CompletedTask;
    }

    private Task SafeSend(MessageEnvelope message)
    {
        try
        {
            return _transport.SendAsync(message);
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }

    private async Task DrainAfterAsync(Task inFlight)
    {
        if (!await Completed(inFlight).ConfigureAwait(false))
        {
            return;
        }

        while (true)
        {
            MessageEnvelope next;
            lock (_lock)
            {
                if (_buffer.Count == 0)
                {
                    _draining = false;
                    return;
                }

                next = _buffer.Dequeue();
            }

            if (!await Completed(SafeSend(next)).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>Awaits a send; on failure marks the channel closed and stops draining. Returns success.</summary>
    private async Task<bool> Completed(Task send)
    {
        try
        {
            await send.ConfigureAwait(false);
            return true;
        }
        catch
        {
            lock (_lock)
            {
                _closed = true;
                _draining = false;
                _buffer.Clear();
            }

            return false;
        }
    }
}
