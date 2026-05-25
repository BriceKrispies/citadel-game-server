using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// End-to-end matchmaking against the real Host (no Docker): a ticket flows through the Host's
/// matchmaking endpoint -> the real Routing allocation adapter (room created + placed on an owning
/// node) -> the real Identity join-token mint. Proves the composition-root wiring of Wave 5 — the two
/// matchmaking ports adapted to the real Wave-4 allocation and the control-plane token mint — works
/// without matchmaking touching the room runtime or taking a sideways dependency on Routing.
/// </summary>
public sealed class MatchmakingScenario
{
    private readonly ITestOutputHelper _output;

    public MatchmakingScenario(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task TwoTicketsInSameScope_AreMatched_IntoOneRoom_WithJoinTokens()
    {
        using var host = new WebApplicationFactory<Program>();
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "dev-tenant-a-key");

        // First ticket queues (no opponent yet); second ticket completes the 2-player match.
        var first = await client.PostAsJsonAsync("/api/v1/matchmaking/tickets",
            new { tenantId = "tenant-a", gameId = "demo-game", gameVersion = 1, playerId = "alice", skill = 10 });
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/v1/matchmaking/tickets",
            new { tenantId = "tenant-a", gameId = "demo-game", gameVersion = 1, playerId = "bob", skill = 11 });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var assignment = await second.Content.ReadFromJsonAsync<JoinTokenDto>();
        Assert.NotNull(assignment);
        Assert.Equal("tenant-a", assignment!.TenantId);
        Assert.Equal("bob", assignment.PlayerId);
        Assert.False(string.IsNullOrWhiteSpace(assignment.Token));
        Assert.False(string.IsNullOrWhiteSpace(assignment.RoomId));

        _output.WriteLine($"matched bob into {assignment.RoomId} with a real join token");
    }

    [Fact]
    public async Task DifferentVersions_NeverMatch_EvenWithTwoWaitingPlayers()
    {
        using var host = new WebApplicationFactory<Program>();
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "dev-tenant-a-key");

        // Same tenant + game, DIFFERENT versions: these must never be put in a room together.
        var v1 = await client.PostAsJsonAsync("/api/v1/matchmaking/tickets",
            new { tenantId = "tenant-a", gameId = "demo-game", gameVersion = 1, playerId = "alice" });
        var v2 = await client.PostAsJsonAsync("/api/v1/matchmaking/tickets",
            new { tenantId = "tenant-a", gameId = "demo-game", gameVersion = 2, playerId = "bob" });

        // Neither matches: each is alone in its own version scope.
        Assert.Equal(HttpStatusCode.Accepted, v1.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, v2.StatusCode);
    }

    [Fact]
    public async Task SubmittingForAnotherTenant_IsForbidden()
    {
        using var host = new WebApplicationFactory<Program>();
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "dev-tenant-a-key"); // scoped to tenant-a

        var crossTenant = await client.PostAsJsonAsync("/api/v1/matchmaking/tickets",
            new { tenantId = "tenant-b", gameId = "demo-game", gameVersion = 1, playerId = "mallory" });

        Assert.Equal(HttpStatusCode.Forbidden, crossTenant.StatusCode);
    }

    private sealed record JoinTokenDto(string Token, string TenantId, string GameId, string RoomId, string PlayerId);
}
