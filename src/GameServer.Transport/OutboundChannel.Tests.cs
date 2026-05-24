using GameServer.Protocol;
using Xunit;

namespace GameServer.Transport;

/// <summary>
/// Contract guards for the <see cref="BoundedOutboundChannel"/> seam. These pin the current
/// (unbuilt) state: construction validates, and every operation reports it is not implemented.
/// When the async outbound buffer is built, these flip to real enqueue/drop/depth behavior.
/// </summary>
public sealed class OutboundChannelTests
{
    private static IServerPushTransport Transport() => new InMemoryBidirectionalTransport(new ConnectionId("c1"));

    [Fact]
    public void Construction_WithNonPositiveCapacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedOutboundChannel(Transport(), capacity: 0));
    }

    [Fact]
    public void TryEnqueue_IsNotImplementedYet()
    {
        var channel = new BoundedOutboundChannel(Transport(), capacity: 8);
        Assert.Throws<NotImplementedException>(() => channel.TryEnqueue(null!));
    }

    [Fact]
    public void QueueDepth_IsNotImplementedYet()
    {
        var channel = new BoundedOutboundChannel(Transport(), capacity: 8);
        Assert.Throws<NotImplementedException>(() => channel.QueueDepth);
    }
}
