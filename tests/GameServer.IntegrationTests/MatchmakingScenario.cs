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
    public async Task QueuedPlayer_MatchedByALaterCycle_CanFetchItsAssignmentByTicketId()
    {
        // The lost-assignment gap: alice submits first and is QUEUED (202 + a Location). bob's submission
        // triggers the cycle that matches BOTH. Before the fix, alice's token was dropped with her ticket
        // and her Location 404'd forever. Now she fetches her room+token from that very Location.
        using var host = new WebApplicationFactory<Program>();
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "dev-tenant-a-key");

        var first = await client.PostAsJsonAsync("/api/v1/matchmaking/tickets",
            new { tenantId = "tenant-a", gameId = "demo-game", gameVersion = 1, playerId = "alice", skill = 10 });
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var location = first.Headers.Location;
        Assert.NotNull(location); // the queued Location must point at a real fetch endpoint

        // While only alice is queued, her Location is a clean 404 (not assigned yet), never a 500.
        var notYet = await client.GetAsync(location);
        Assert.Equal(HttpStatusCode.NotFound, notYet.StatusCode);

        // bob completes the 2-player match (this cycle assigns alice too).
        var second = await client.PostAsJsonAsync("/api/v1/matchmaking/tickets",
            new { tenantId = "tenant-a", gameId = "demo-game", gameVersion = 1, playerId = "bob", skill = 11 });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        // Alice polls her Location again and now gets her assignment — no longer stranded.
        var fetched = await client.GetAsync(location);
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        var assignment = await fetched.Content.ReadFromJsonAsync<JoinTokenDto>();
        Assert.Equal("alice", assignment!.PlayerId);
        Assert.False(string.IsNullOrWhiteSpace(assignment.Token));
        Assert.False(string.IsNullOrWhiteSpace(assignment.RoomId));

        _output.WriteLine($"alice fetched her late assignment: room={assignment.RoomId}");
    }

    [Fact]
    public async Task FetchingAnotherTenantsAssignment_IsForbidden_NotLeaked()
    {
        // alice (tenant-a) submits and is queued; her real ticket id is in the Location. bob completes the
        // match so alice now HAS an assignment. A tenant-b caller fetching alice's real ticket id must be
        // forbidden — the fetch authorizes against the assignment's OWNING tenant, so a token is never
        // handed to the wrong tenant even with the exact id.
        using var host = new WebApplicationFactory<Program>();
        var a = host.CreateClient();
        a.DefaultRequestHeaders.Authorization = new("Bearer", "dev-tenant-a-key");
        var b = host.CreateClient();
        b.DefaultRequestHeaders.Authorization = new("Bearer", "dev-tenant-b-key");

        var queued = await a.PostAsJsonAsync("/api/v1/matchmaking/tickets",
            new { tenantId = "tenant-a", gameId = "demo-game", gameVersion = 1, playerId = "alice" });
        var aliceLocation = queued.Headers.Location!; // /api/v1/matchmaking/tickets/{alice's real ticketId}

        await a.PostAsJsonAsync("/api/v1/matchmaking/tickets",
            new { tenantId = "tenant-a", gameId = "demo-game", gameVersion = 1, playerId = "bob" });

        // tenant-a CAN fetch its own assignment (the assignment exists).
        var ownerFetch = await a.GetAsync(aliceLocation);
        Assert.Equal(HttpStatusCode.OK, ownerFetch.StatusCode);

        // tenant-b fetching the SAME (existing) tenant-a assignment id is forbidden — no token leak.
        var crossTenantFetch = await b.GetAsync(aliceLocation);
        Assert.Equal(HttpStatusCode.Forbidden, crossTenantFetch.StatusCode);
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
