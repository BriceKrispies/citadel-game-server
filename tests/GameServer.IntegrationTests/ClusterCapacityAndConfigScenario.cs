using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// Covers the clustering wiring the happy-path tests don't: room creation shedding cleanly when the
/// cluster is full (503), and the Redis backend failing fast on misconfiguration. Both run against the
/// real Host via <see cref="WebApplicationFactory{TEntryPoint}"/>; no Docker needed.
/// </summary>
public sealed class ClusterCapacityAndConfigScenario
{
    private readonly ITestOutputHelper _output;

    public ClusterCapacityAndConfigScenario(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task RoomCreation_BeyondNodeCapacity_Returns503_WithNoOwnerHeader()
    {
        // Single-node fleet (default), one room slot. The second room cannot be placed anywhere.
        using var host = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("Cluster:MaxRoomsPerNode", "1"));
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "dev-tenant-a-key");

        var first = await client.PostAsJsonAsync("/api/v1/rooms", new { tenantId = "tenant-a", gameId = "demo-game" });
        var second = await client.PostAsJsonAsync("/api/v1/rooms", new { tenantId = "tenant-a", gameId = "demo-game" });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.True(first.Headers.Contains("X-Citadel-Owner-Node"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        var error = await second.Content.ReadFromJsonAsync<ApiErrorDto>();
        Assert.Equal("ClusterAtCapacity", error!.Code);
        Assert.False(second.Headers.Contains("X-Citadel-Owner-Node"));
    }

    [Fact]
    public async Task DrainNodeEndpoint_RequiresPlatformAdmin_NonAdminIs403_AdminSucceeds()
    {
        // The drain endpoint acts on fleet topology, so it is platform-admin only. Prove the authz gate:
        // unauthenticated -> 401 (group filter), a tenant-scoped non-admin -> 403, a platform admin -> 200.
        using var host = new WebApplicationFactory<Program>();
        var anon = host.CreateClient();
        var tenantOperator = host.CreateClient();
        tenantOperator.DefaultRequestHeaders.Authorization = new("Bearer", "dev-tenant-a-key"); // no roles
        var admin = host.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new("Bearer", "dev-admin-key"); // platform-admin

        var node = Uri.EscapeDataString("http://localhost:5000");

        var unauth = await anon.PostAsync($"/api/v1/admin/nodes/{node}/drain", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);

        var forbidden = await tenantOperator.PostAsync($"/api/v1/admin/nodes/{node}/drain", content: null);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        var error = await forbidden.Content.ReadFromJsonAsync<ApiErrorDto>();
        Assert.Equal("Forbidden", error!.Code);

        var ok = await admin.PostAsync($"/api/v1/admin/nodes/{node}/drain", content: null);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = await ok.Content.ReadFromJsonAsync<DrainResponseDto>();
        Assert.True(body!.Draining);

        _output.WriteLine($"drain authz: anon=401, tenant-operator=403, platform-admin=200 (draining={body.Draining})");
    }

    [Fact]
    public void RedisBackend_WithoutConnectionString_FailsFastAtStartup()
    {
        // Selecting the Redis backend with no connection string is a misconfiguration the node must
        // refuse loudly at startup, not discover on the first connect.
        using var host = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("Cluster:Backend", "Redis"));

        var ex = Record.Exception(() => host.CreateClient());

        Assert.NotNull(ex);
        Assert.Contains("Cluster:Redis:ConnectionString", ex!.ToString());
    }

    private sealed record ApiErrorDto(string Code, string Message);

    private sealed record DrainResponseDto(string Node, bool Draining, int ShedCount);
}
