using System.Net.Http.Headers;

namespace GameServer.SecurityTests;

/// <summary>
/// The black-box target + credentials, read ONCE from the environment that
/// <c>scripts/security-test.ps1</c> exports. When <see cref="Configured"/> is false the
/// whole suite skips (every test opens with <c>Skip.IfNot(Target.Configured, ...)</c>), so a
/// plain repo-wide <c>dotnet test</c> with no container running stays green.
/// </summary>
/// <remarks>
/// This is a value object, not a fixture with disposable state: the suite owns no server, it
/// only attacks one already running at <see cref="BaseUrl"/>. Reading the env in one place keeps
/// the exact variable names (the contract with the harness script) in a single spot.
/// </remarks>
public sealed class SecurityTarget
{
    public const string SkipReason =
        "CITADEL_SECURITY_TARGET is not set; this black-box suite only runs against a live container " +
        "(see scripts/security-test.ps1).";

    public static readonly SecurityTarget Current = ReadFromEnvironment();

    /// <summary>
    /// Build a target with an EXPLICIT base URL + credentials instead of the env contract. Used by the
    /// end-to-end suite, which starts the hardened image via Testcontainers and only knows the
    /// dynamically-mapped host port at runtime (so it cannot rely on <c>CITADEL_SECURITY_TARGET</c>).
    /// </summary>
    public static SecurityTarget ForExplicit(
        string baseUrl, string tenantAKey, string tenantBKey, string adminKey, string joinSecret) =>
        new(baseUrl, tenantAKey, tenantBKey, adminKey, joinSecret);

    private SecurityTarget(string? baseUrl, string? tenantAKey, string? tenantBKey, string? adminKey, string? joinSecret)
    {
        BaseUrl = baseUrl;
        TenantAKey = tenantAKey;
        TenantBKey = tenantBKey;
        AdminKey = adminKey;
        JoinSecret = joinSecret;
    }

    /// <summary>True when a target is configured and the suite should run.</summary>
    public bool Configured => !string.IsNullOrWhiteSpace(BaseUrl);

    /// <summary>Base HTTP URL, e.g. <c>http://localhost:8080</c>.</summary>
    public string? BaseUrl { get; }

    /// <summary>tenant-a control-plane API key (config index 0, TenantId=tenant-a).</summary>
    public string? TenantAKey { get; }

    /// <summary>tenant-b control-plane API key (config index 1, TenantId=tenant-b).</summary>
    public string? TenantBKey { get; }

    /// <summary>platform-admin API key (config index 2, TenantId=tenant-a, Roles[0]=platform-admin).</summary>
    public string? AdminKey { get; }

    /// <summary>The real HS256 join-token signing secret the container uses.</summary>
    public string? JoinSecret { get; }

    public const string TenantA = "tenant-a";
    public const string TenantB = "tenant-b";

    /// <summary>A fresh <see cref="HttpClient"/> pointed at the target. Caller disposes.</summary>
    public HttpClient NewHttpClient()
    {
        return new HttpClient { BaseAddress = new Uri(BaseUrl!), Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>An <see cref="HttpClient"/> whose default Authorization is the given bearer key.</summary>
    public HttpClient NewHttpClient(string? bearerKey)
    {
        var client = NewHttpClient();
        if (bearerKey is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerKey);
        }

        return client;
    }

    /// <summary>The ws:// base derived from the http(s):// target.</summary>
    public Uri WebSocketUri(string pathAndQuery)
    {
        var http = new Uri(BaseUrl!);
        var scheme = http.Scheme == "https" ? "wss" : "ws";
        return new Uri($"{scheme}://{http.Authority}{pathAndQuery}");
    }

    private static SecurityTarget ReadFromEnvironment() => new(
        Environment.GetEnvironmentVariable("CITADEL_SECURITY_TARGET"),
        Environment.GetEnvironmentVariable("CITADEL_TEST_APIKEY"),
        Environment.GetEnvironmentVariable("CITADEL_TEST_APIKEY_B"),
        Environment.GetEnvironmentVariable("CITADEL_TEST_ADMINKEY"),
        Environment.GetEnvironmentVariable("CITADEL_TEST_JOINSECRET"));
}
