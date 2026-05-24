using Xunit;

namespace GameServer.ControlPlane;

public sealed class ApiKeyControlPlaneAuthenticatorTests
{
    private static ApiKeyControlPlaneAuthenticator Authenticator() =>
        new(new Dictionary<string, CallerPrincipal>
        {
            ["key-a"] = new("operator-a", "tenant-a", new HashSet<string>()),
        });

    [Fact]
    public void ValidBearerKey_AuthenticatesToItsPrincipal()
    {
        Assert.True(Authenticator().TryAuthenticate("Bearer key-a", out var caller));
        Assert.Equal("operator-a", caller.CallerId);
        Assert.Equal("tenant-a", caller.TenantId);
    }

    [Fact]
    public void Scheme_IsCaseInsensitive()
    {
        Assert.True(Authenticator().TryAuthenticate("bearer key-a", out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("key-a")]            // missing scheme
    [InlineData("Basic key-a")]      // wrong scheme
    [InlineData("Bearer ")]          // empty key
    [InlineData("Bearer unknown")]   // unknown key
    public void MissingOrInvalidCredentials_FailClosed(string? header)
    {
        Assert.False(Authenticator().TryAuthenticate(header, out var caller));
        Assert.Null(caller);
    }
}
