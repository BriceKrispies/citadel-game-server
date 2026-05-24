using Xunit;

namespace GameServer.Identity;

public sealed class JoinTokensTests
{
    private static JoinTokenClaims Claims() =>
        new("tenant-a", "demo-game", "room-1", "player-1",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1));

    [Fact]
    public void Ok_CarriesClaims_AndIsOk()
    {
        var verification = TokenVerification.Ok(Claims());

        Assert.True(verification.IsOk);
        Assert.Equal(TokenVerificationStatus.Ok, verification.Status);
        Assert.Equal("player-1", verification.Claims!.PlayerId);
    }

    [Theory]
    [InlineData(TokenVerificationStatus.Expired)]
    [InlineData(TokenVerificationStatus.BadSignature)]
    [InlineData(TokenVerificationStatus.Malformed)]
    public void Fail_HasNoClaims_AndIsNotOk(TokenVerificationStatus status)
    {
        var verification = TokenVerification.Fail(status);

        Assert.False(verification.IsOk);
        Assert.Equal(status, verification.Status);
        Assert.Null(verification.Claims);
    }

    [Fact]
    public void Claims_AreValueEqual()
    {
        Assert.Equal(Claims(), Claims());
    }
}
