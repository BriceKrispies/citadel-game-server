using GameServer.Identity;
using GameServer.Identity.Testing;
using Xunit;

namespace GameServer.ControlPlane;

public sealed class JoinTokenServiceTests
{
    private static Hs256JoinTokenCodec Codec() =>
        new("control-plane-test-secret", new FakeClock(DateTimeOffset.UnixEpoch.AddDays(1)), TimeSpan.FromMinutes(2));

    [Fact]
    public void Issue_ReturnsContract_WithAVerifiableSignedToken()
    {
        var codec = Codec();
        var service = new JoinTokenService(codec);

        var contract = service.Issue("tenant-a", "demo-game", "room-1", "player-1");

        Assert.False(string.IsNullOrEmpty(contract.Token));
        Assert.Equal("tenant-a", contract.TenantId);
        Assert.Equal("demo-game", contract.GameId);
        Assert.Equal("room-1", contract.RoomId);
        Assert.Equal("player-1", contract.PlayerId);

        // The minted token must verify back to exactly the scoped claims.
        var verified = codec.Verify(contract.Token);
        Assert.True(verified.IsOk);
        Assert.Equal("tenant-a", verified.Claims!.TenantId);
        Assert.Equal("room-1", verified.Claims.RoomId);
        Assert.Equal("player-1", verified.Claims.PlayerId);
    }
}
