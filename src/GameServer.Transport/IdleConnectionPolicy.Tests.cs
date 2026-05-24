using Xunit;

namespace GameServer.Transport;

/// <summary>
/// Contract guards for the <see cref="HeartbeatIdlePolicy"/> seam: construction validates the
/// interval/timeout ordering, and evaluation reports it is not implemented yet.
/// </summary>
public sealed class IdleConnectionPolicyTests
{
    [Fact]
    public void Construction_WithTimeoutNotBeyondHeartbeat_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new HeartbeatIdlePolicy(heartbeatInterval: TimeSpan.FromSeconds(10), idleTimeout: TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Evaluate_IsNotImplementedYet()
    {
        var policy = new HeartbeatIdlePolicy(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60));
        Assert.Throws<NotImplementedException>(() => policy.Evaluate(TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(90)));
    }
}
