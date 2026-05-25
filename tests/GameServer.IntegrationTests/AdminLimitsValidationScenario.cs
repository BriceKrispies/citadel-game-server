using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// Wave 8 hardening: the runtime limits-config endpoint must reject degenerate ceilings (0/negative)
/// rather than apply a self-inflicted outage, and the host must not advertise its server stack in the
/// HTTP response banner. Both run against the real Host via <see cref="WebApplicationFactory{T}"/>.
/// </summary>
public sealed class AdminLimitsValidationScenario
{
    private const string PlatformAdmin = "dev-admin-key";

    private readonly ITestOutputHelper _output;

    public AdminLimitsValidationScenario(ITestOutputHelper output) => _output = output;

    private static HttpClient Admin(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", PlatformAdmin);
        return client;
    }

    [Theory]
    [InlineData(0, 100, 50, 10)]
    [InlineData(-1, 100, 50, 10)]
    [InlineData(100, 0, 50, 10)]
    [InlineData(100, 100, 0, 10)]
    [InlineData(100, 100, 50, -5)]
    public async Task PutLimits_RejectsDegenerateCeilings_With400_AndDoesNotApplyThem(
        int maxConnections, int maxConnectionsPerTenant, int maxRooms, int maxRoomsPerTenant)
    {
        using var host = new WebApplicationFactory<Program>();
        var client = Admin(host);

        var before = await (await client.GetAsync("/api/v1/admin/limits"))
            .Content.ReadFromJsonAsync<AdmissionLimitsDto>();

        var resp = await client.PutAsJsonAsync("/api/v1/admin/limits",
            new { maxConnections, maxConnectionsPerTenant, maxRooms, maxRoomsPerTenant });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var error = await resp.Content.ReadFromJsonAsync<ApiErrorDto>();
        Assert.Equal("InvalidLimits", error!.Code);

        // The bad update must NOT have taken effect — the live ceilings are unchanged.
        var after = await (await client.GetAsync("/api/v1/admin/limits"))
            .Content.ReadFromJsonAsync<AdmissionLimitsDto>();
        Assert.Equal(before!.MaxConnections, after!.MaxConnections);
        Assert.Equal(before.MaxRooms, after.MaxRooms);
    }

    [Fact]
    public async Task PutLimits_AcceptsValidCeilings_AndAppliesThem()
    {
        using var host = new WebApplicationFactory<Program>();
        var client = Admin(host);

        var resp = await client.PutAsJsonAsync("/api/v1/admin/limits",
            new { maxConnections = 777, maxConnectionsPerTenant = 50, maxRooms = 25, maxRoomsPerTenant = 5 });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var updated = await resp.Content.ReadFromJsonAsync<AdmissionLimitsDto>();
        Assert.Equal(777, updated!.MaxConnections);
    }

    [Fact]
    public async Task Host_DoesNotAdvertiseServerStack_InResponseBanner()
    {
        using var host = new WebApplicationFactory<Program>();
        var client = host.CreateClient();

        var resp = await client.GetAsync("/health");

        Assert.False(resp.Headers.Contains("Server"),
            "the host must not emit a Server response header (stack fingerprinting).");
        _output.WriteLine($"/health Server header present: {resp.Headers.Contains("Server")}");
    }

    private sealed record AdmissionLimitsDto(int MaxConnections, int MaxConnectionsPerTenant, int MaxRooms, int MaxRoomsPerTenant);

    private sealed record ApiErrorDto(string Code, string Message);
}
