using System.Net;
using System.Net.Http.Json;
using GameServer.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// End-to-end proof of the Host clustering wiring: two real Host instances sharing ONE room directory
/// (what a fleet sharing Redis would be) with distinct node identities. A room is created+placed on
/// node-A; a client that connects to node-B (the non-owner) is redirected (409 + node-A's address)
/// before any socket is accepted, while a connect to node-A passes affinity. This is the split-brain
/// fix observed through the real HTTP edge, without Docker — the shared in-memory directory stands in
/// for the distributed one (covered separately by <see cref="RedisRoomDirectoryScenario"/>).
/// </summary>
public sealed class ClusterRoutingHostScenario
{
    private const string NodeA = "http://node-a";
    private const string NodeB = "http://node-b";

    private readonly ITestOutputHelper _output;

    public ClusterRoutingHostScenario(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task ConnectToNonOwner_IsRedirectedToTheOwningNode()
    {
        // One directory instance shared by both "nodes" — modelling shared cluster infrastructure.
        var sharedDirectory = new InMemoryRoomDirectory();

        using var nodeA = HostFor(NodeA, sharedDirectory);
        using var nodeB = HostFor(NodeB, sharedDirectory);

        var operatorA = nodeA.CreateClient();
        operatorA.DefaultRequestHeaders.Authorization = new("Bearer", "dev-tenant-a-key");

        // Create + place a room via node-A's control plane. With fleet order [A, B] it lands on node-A.
        var create = await operatorA.PostAsJsonAsync("/api/v1/rooms", new { tenantId = "tenant-a", gameId = "demo-game" });
        create.EnsureSuccessStatusCode();
        Assert.Equal(NodeA, create.Headers.GetValues("X-Citadel-Owner-Node").Single());
        var roomId = (await create.Content.ReadFromJsonAsync<RoomDto>())!.RoomId;

        // Mint a join token for that room.
        var mint = await operatorA.PostAsJsonAsync($"/api/v1/rooms/{roomId}/join-token", new { playerId = "p1" });
        mint.EnsureSuccessStatusCode();
        Assert.Equal(NodeA, mint.Headers.GetValues("X-Citadel-Owner-Node").Single()); // owner hint for direct connect
        var token = (await mint.Content.ReadFromJsonAsync<TokenDto>())!.Token;
        var connect = $"/realtime/v1/connect?joinToken={Uri.EscapeDataString(token)}";

        // Connecting to node-B (NOT the owner) is redirected to node-A — before any socket is accepted.
        var atNonOwner = await nodeB.CreateClient().GetAsync(connect);
        Assert.Equal(HttpStatusCode.Conflict, atNonOwner.StatusCode);
        var redirect = await atNonOwner.Content.ReadFromJsonAsync<WrongNodeDto>();
        Assert.Equal("WrongNode", redirect!.Error);
        Assert.Equal(NodeA, redirect.OwnerNode);

        // Connecting to node-A (the owner) passes affinity; it then fails only the WebSocket-upgrade
        // guard (a plain GET is not an upgrade) — proving the owner would serve a real WS client.
        var atOwner = await nodeA.CreateClient().GetAsync(connect);
        Assert.Equal(HttpStatusCode.BadRequest, atOwner.StatusCode);
        Assert.NotEqual(HttpStatusCode.Conflict, atOwner.StatusCode);

        _output.WriteLine($"room {roomId} owned by {NodeA}: node-B redirected (409→{redirect.OwnerNode}), node-A passed affinity (400 upgrade-required)");
    }

    private static WebApplicationFactory<Program> HostFor(string nodeId, IRoomDirectory shared) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Cluster:NodeId", nodeId);
            builder.UseSetting("Cluster:Nodes:0", NodeA);
            builder.UseSetting("Cluster:Nodes:1", NodeB);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IRoomDirectory>();
                services.AddSingleton<IRoomDirectory>(shared);
            });
        });

    private sealed record RoomDto(string RoomId);

    private sealed record TokenDto(string Token);

    private sealed record WrongNodeDto(string Error, string OwnerNode);
}
