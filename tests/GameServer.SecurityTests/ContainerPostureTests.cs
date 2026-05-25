using System.Net;
using System.Net.Http.Json;

namespace GameServer.SecurityTests;

/// <summary>
/// Production container posture (confirmed finding #2): the dev-only endpoints must be absent, the
/// public diagnostic endpoints must not over-disclose, and the unauthenticated surface should not
/// be discoverable. <c>/ws</c> trusts query-string identity with no token and <c>/sim/telemetry</c>
/// streams aggregate telemetry — both are gated by <c>IsDevelopment() AND Realtime:EnableDevEndpoints</c>,
/// so a Production image never maps them (404).
/// </summary>
public sealed class ContainerPostureTests
{
    private static readonly SecurityTarget Target = SecurityTarget.Current;

    private static CancellationToken Ct => new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token;

    [SkippableFact]
    public async Task DevWebSocketEndpoint_Is404()
    {
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);
        using var client = Target.NewHttpClient();
        var resp = await client.GetAsync("/ws", Ct);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [SkippableFact]
    public async Task DevTelemetryStream_Is404()
    {
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);
        using var client = Target.NewHttpClient();
        var resp = await client.GetAsync("/sim/telemetry", Ct);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [SkippableFact]
    public async Task Health_Is200_AndDoesNotDiscloseInternals()
    {
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);
        using var client = Target.NewHttpClient();
        var resp = await client.GetAsync("/health", Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // The body is a fixed liveness token, not a dependency dump (no secrets/paths/versions).
        var body = await resp.Content.ReadAsStringAsync(Ct);
        Assert.Contains("healthy", body);
        Assert.DoesNotContain("Secret", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ApiKey", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ConnectionString", body, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Ready_Is200()
    {
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);
        using var client = Target.NewHttpClient();
        var resp = await client.GetAsync("/ready", Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [SkippableFact]
    public async Task Version_DisclosesOnlyProtocolMetadata()
    {
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);
        using var client = Target.NewHttpClient();
        var resp = await client.GetAsync("/version", Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // /version is intentionally public and advertises protocol metadata so clients can
        // negotiate. It must not leak build paths, secrets, or host/runtime internals.
        var body = await resp.Content.ReadAsStringAsync(Ct);
        Assert.Contains("protocolVersion", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Secret", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\", body);
        Assert.DoesNotContain("/app", body);
    }

    [SkippableFact]
    public async Task ResponseHeaders_DoNotLeakServerStack()
    {
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);
        using var client = Target.NewHttpClient();
        var resp = await client.GetAsync("/health", Ct);

        // Kestrel does not emit a Server banner by default; assert no stack-revealing headers crept
        // in. (X-Powered-By is an ASP.NET Framework artifact and must never appear here.)
        Assert.False(resp.Headers.Contains("X-Powered-By"), "X-Powered-By header should not be present.");
        var server = resp.Headers.Server.ToString();
        Assert.DoesNotContain("Microsoft", server, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task UnknownPath_Is404_WithoutStackTrace()
    {
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);
        using var client = Target.NewHttpClient();
        var resp = await client.GetAsync("/" + Guid.NewGuid().ToString("n"), Ct);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(Ct);
        // A production 404 must not include a developer exception page / stack trace.
        Assert.DoesNotContain("StackTrace", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("at GameServer.", body);
    }
}
