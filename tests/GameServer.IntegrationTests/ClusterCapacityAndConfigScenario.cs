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
}
