using GameServer.Protocol;
using Xunit;

namespace GameServer.Transport;

public sealed class OutboundChannelTests
{
    [Fact]
    public void Construction_WithNonPositiveCapacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedOutboundChannel(new GateTransport(), capacity: 0));
    }

    [Fact]
    public void ReadySocket_DeliversInline_InOrder()
    {
        var transport = new GateTransport(); // ungated: sends complete synchronously
        var channel = new BoundedOutboundChannel(transport, capacity: 8);

        Assert.Equal(OutboundEnqueueResult.Enqueued, channel.TryEnqueue(Message("a")));
        Assert.Equal(OutboundEnqueueResult.Enqueued, channel.TryEnqueue(Message("b")));

        Assert.Equal(new[] { "a", "b" }, transport.Sent);
        Assert.Equal(0, channel.QueueDepth);
    }

    [Fact]
    public async Task SlowClient_DoesNotBlockEnqueue_AndDrainsInOrderOnceUnblocked()
    {
        var transport = new GateTransport();
        var channel = new BoundedOutboundChannel(transport, capacity: 8);

        transport.Block(); // the socket stalls
        // None of these block the caller even though the socket is stalled.
        Assert.Equal(OutboundEnqueueResult.Enqueued, channel.TryEnqueue(Message("a")));
        Assert.Equal(OutboundEnqueueResult.Enqueued, channel.TryEnqueue(Message("b")));
        Assert.Equal(OutboundEnqueueResult.Enqueued, channel.TryEnqueue(Message("c")));

        transport.Release(); // socket recovers; background writer flushes in order
        await transport.WaitForSent(3);
        Assert.Equal(new[] { "a", "b", "c" }, transport.Sent);
    }

    [Fact]
    public void FullBuffer_DropsNewestAndCounts_NeverBlocking()
    {
        var transport = new GateTransport();
        var channel = new BoundedOutboundChannel(transport, capacity: 2);

        transport.Block();
        channel.TryEnqueue(Message("inflight")); // becomes the in-flight send
        channel.TryEnqueue(Message("buf-1"));     // buffered (1/2)
        channel.TryEnqueue(Message("buf-2"));     // buffered (2/2)

        Assert.Equal(OutboundEnqueueResult.DroppedQueueFull, channel.TryEnqueue(Message("overflow")));
        Assert.Equal(1, channel.DroppedCount);
        transport.Release();
    }

    private static MessageEnvelope Message(string trace) =>
        new()
        {
            TenantId = new TenantId("t"),
            GameId = new GameId("g"),
            ProtocolVersion = ProtocolVersions.Current,
            MessageType = MessageType.ServerSnapshot,
            Sequence = 0,
            TraceId = trace,
            Payload = new ServerSnapshot(0, Array.Empty<EntityState>()),
        };

    /// <summary>A push transport that records sent trace ids and can be blocked to model a stalled socket.</summary>
    private sealed class GateTransport : IServerPushTransport
    {
        private readonly object _lock = new();
        private readonly List<string> _sent = new();
        private TaskCompletionSource? _gate;

        public ConnectionId ConnectionId { get; } = new("gate");

        public IReadOnlyList<string> Sent
        {
            get { lock (_lock) { return _sent.ToList(); } }
        }

        public void Block() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate?.TrySetResult();

        public async Task SendAsync(MessageEnvelope message, CancellationToken cancellationToken = default)
        {
            var gate = _gate;
            if (gate is not null)
            {
                await gate.Task.ConfigureAwait(false);
            }

            lock (_lock)
            {
                _sent.Add(message.TraceId);
            }
        }

        public async Task WaitForSent(int count)
        {
            for (var i = 0; i < 200; i++)
            {
                lock (_lock)
                {
                    if (_sent.Count >= count)
                    {
                        return;
                    }
                }

                await Task.Delay(10);
            }

            throw new TimeoutException($"Only {Sent.Count} of {count} messages were sent.");
        }
    }
}
