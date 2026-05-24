using GameServer.Protocol;
using Xunit;

namespace GameServer.Transport;

/// <summary>
/// Locks in the Liskov-honest transport split: a server-push-only transport
/// (SSE/HTTP-push/spectator) must never be forced to receive client commands.
/// </summary>
public sealed class TransportContractTests
{
    [Fact]
    public void PushOnlyTransport_DoesNotImplement_CommandReceiver()
    {
        var pushOnly = new BufferedServerPushTransport(new ConnectionId("sse-1"));

        Assert.IsAssignableFrom<IServerPushTransport>(pushOnly);
        Assert.IsNotAssignableFrom<IClientCommandReceiver>(pushOnly);
    }

    [Fact]
    public void PushTransportContract_HasNoReceiveCapability()
    {
        // The push-only contract exposes sending but no way to receive: the
        // capability simply does not exist on it to be mis-implemented.
        Assert.Null(typeof(IServerPushTransport).GetMethod(nameof(IClientCommandReceiver.ReceiveAsync)));
        Assert.NotNull(typeof(IServerPushTransport).GetMethod(nameof(IServerPushTransport.SendAsync)));
    }

    [Fact]
    public void BidirectionalTransport_Satisfies_BothCapabilities()
    {
        var duplex = new InMemoryBidirectionalTransport(new ConnectionId("ws-1"));

        Assert.IsAssignableFrom<IServerPushTransport>(duplex);
        Assert.IsAssignableFrom<IClientCommandReceiver>(duplex);
    }
}
