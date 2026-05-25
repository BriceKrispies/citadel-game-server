using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace GameServer.SecurityTests;

/// <summary>
/// The control-plane 401 matrix against the PRODUCTION image: every <c>/api/v1</c> call must
/// authenticate, and the hardcoded dev keys that exist only in the Development branch of
/// <c>Program.cs</c> must NOT authenticate against a prod container (confirmed finding #1).
/// </summary>
public sealed class ControlPlaneAuthTests
{
    private static readonly SecurityTarget Target = SecurityTarget.Current;

    // A protected endpoint behind the group auth filter; reading the catalog requires a valid key.
    private const string ProtectedPath = "/api/v1/games";

    /// <summary>The literal dev keys seeded ONLY in <c>Program.cs</c>'s Development branch.</summary>
    public static IEnumerable<object[]> DevKeys() => new[]
    {
        new object[] { "dev-tenant-a-key" },
        new object[] { "dev-tenant-b-key" },
        new object[] { "dev-admin-key" },
    };

    [SkippableFact]
    public async Task MissingAuthorization_Is401()
    {
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);
        using var client = Target.NewHttpClient();
        var resp = await client.GetAsync(ProtectedPath);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [SkippableFact]
    public async Task GarbageBearerKey_Is401()
    {
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);
        using var client = Target.NewHttpClient("not-a-real-key-" + Guid.NewGuid().ToString("n"));
        var resp = await client.GetAsync(ProtectedPath);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [SkippableFact]
    public async Task WrongScheme_Is401()
    {
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);
        using var client = Target.NewHttpClient();
        // A valid key presented under the wrong scheme (Basic) must not authenticate.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Target.TenantAKey);
        var resp = await client.GetAsync(ProtectedPath);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [SkippableFact]
    public async Task RawKeyWithoutBearer_Is401()
    {
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);
        using var client = Target.NewHttpClient();
        // The header is the bare key with no "Bearer " prefix.
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", Target.TenantAKey);
        var resp = await client.GetAsync(ProtectedPath);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [SkippableTheory]
    [MemberData(nameof(DevKeys))]
    public async Task HardcodedDevKeys_AreRejected(string devKey)
    {
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);
        using var client = Target.NewHttpClient(devKey);
        var resp = await client.GetAsync(ProtectedPath);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [SkippableFact]
    public async Task ConfiguredTenantKey_Authenticates()
    {
        // Positive control: the real injected key DOES authenticate, proving the 401s above are
        // about credentials, not a broken endpoint.
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);
        using var client = Target.NewHttpClient(Target.TenantAKey);
        var resp = await client.GetAsync(ProtectedPath);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}
