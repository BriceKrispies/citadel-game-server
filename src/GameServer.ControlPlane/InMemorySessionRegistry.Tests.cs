using Xunit;

namespace GameServer.ControlPlane;

public sealed class InMemorySessionRegistryTests
{
    [Fact]
    public void Create_Then_Get_Then_Remove()
    {
        var registry = new InMemorySessionRegistry();

        var session = registry.Create(new CreateSessionRequest("tenant-a", "player-1"));

        Assert.Equal("tenant-a", session.TenantId);
        Assert.Equal("active", session.Status);
        Assert.True(registry.TryGet(session.SessionId, out _));

        Assert.True(registry.Remove(session.SessionId));
        Assert.False(registry.TryGet(session.SessionId, out _));
    }
}
