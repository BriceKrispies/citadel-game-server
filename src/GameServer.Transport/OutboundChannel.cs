using GameServer.Protocol;

namespace GameServer.Transport;

/// <summary>What happened when fan-out tried to hand a message to a connection's outbound buffer.</summary>
public enum OutboundEnqueueResult
{
    /// <summary>Buffered; a background writer will flush it to the socket.</summary>
    Enqueued,

    /// <summary>The buffer was full (a slow/stalled client); the message was dropped, not awaited.</summary>
    DroppedQueueFull,

    /// <summary>The connection is closing/closed; nothing was buffered.</summary>
    DroppedClosed,
}

/// <summary>
/// A per-connection, bounded, non-blocking outbound buffer that decouples the room tick
/// from socket I/O. Today <see cref="RealtimeServer.TickRoom"/> fans out by <c>await</c>ing
/// each connection's <see cref="IServerPushTransport.SendAsync"/> inline, so one slow client
/// stalls the whole room's tick (head-of-line blocking) and a stalled client implies
/// unbounded server-side buffering. The hardened design hands each snapshot to this channel:
/// <see cref="TryEnqueue"/> returns immediately, a background writer drains to the socket,
/// and a full buffer drops (and is observable) rather than blocking the authoritative loop.
/// </summary>
/// <remarks>
/// RED-phase seam: the contract exists so the slow-client fan-out behavior can be pinned by a
/// test, but the implementation is intentionally not written yet. Wiring it into
/// <see cref="RealtimeServer"/> (one channel per <c>Connection</c>) is the corresponding
/// production change. See <c>SlowClientFanoutScenario</c>.
/// </remarks>
public interface IOutboundChannel : IAsyncDisposable
{
    /// <summary>Messages currently buffered and not yet flushed to the socket.</summary>
    int QueueDepth { get; }

    /// <summary>Messages dropped so far because the buffer was full (a backpressure signal).</summary>
    long DroppedCount { get; }

    /// <summary>
    /// Buffers a message for asynchronous delivery. Never blocks on socket I/O and never
    /// throws on a slow client — a full buffer yields <see cref="OutboundEnqueueResult.DroppedQueueFull"/>.
    /// </summary>
    OutboundEnqueueResult TryEnqueue(MessageEnvelope message);
}

/// <summary>
/// Bounded outbound buffer over an <see cref="IServerPushTransport"/>. Drops the oldest (or
/// rejects the newest) when full so a slow reader can never grow server memory without bound.
/// </summary>
public sealed class BoundedOutboundChannel : IOutboundChannel
{
    private const string NotBuilt =
        "BoundedOutboundChannel is a RED-phase seam: per-connection async outbound buffering is not implemented yet.";

    private readonly IServerPushTransport _transport;
    private readonly int _capacity;

    public BoundedOutboundChannel(IServerPushTransport transport, int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Outbound capacity must be positive.");
        }

        _transport = transport;
        _capacity = capacity;
    }

    public int QueueDepth => throw new NotImplementedException(NotBuilt);

    public long DroppedCount => throw new NotImplementedException(NotBuilt);

    public OutboundEnqueueResult TryEnqueue(MessageEnvelope message) => throw new NotImplementedException(NotBuilt);

    public ValueTask DisposeAsync() => throw new NotImplementedException(NotBuilt);
}
