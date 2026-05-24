using Xunit;

namespace GameServer.Transport;

public sealed class IdleConnectionPolicyTests
{
    private static HeartbeatIdlePolicy Policy() =>
        new(heartbeatInterval: TimeSpan.FromSeconds(15), idleTimeout: TimeSpan.FromSeconds(60));

    [Fact]
    public void Construction_WithTimeoutNotBeyondHeartbeat_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new HeartbeatIdlePolicy(heartbeatInterval: TimeSpan.FromSeconds(10), idleTimeout: TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Active_Connection_IsKeptAlive()
    {
        Assert.Equal(IdleAction.KeepAlive, Policy().Evaluate(
            sinceLastInbound: TimeSpan.FromSeconds(2), sinceLastHeartbeat: TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Quiet_PastHeartbeatInterval_SendsHeartbeat()
    {
        Assert.Equal(IdleAction.SendHeartbeat, Policy().Evaluate(
            sinceLastInbound: TimeSpan.FromSeconds(20), sinceLastHeartbeat: TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void Quiet_PastIdleTimeout_Disconnects()
    {
        Assert.Equal(IdleAction.Disconnect, Policy().Evaluate(
            sinceLastInbound: TimeSpan.FromSeconds(90), sinceLastHeartbeat: TimeSpan.FromSeconds(90)));
    }
}
