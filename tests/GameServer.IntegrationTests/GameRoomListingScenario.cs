using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// The tenant-facing room-discovery endpoint <c>GET /api/v1/games/{gameId}/rooms</c> — Part 1 of the
/// end-to-end lifecycle. An authed client lists the AVAILABLE rooms for a game and ALWAYS gets at least
/// one (lazy auto-ensure), each carrying a snapshot of its live state; the list is strictly tenant-scoped
/// (no cross-tenant leak). Boots the real <c>Program.cs</c> wiring in-process via
/// <see cref="WebApplicationFactory{T}"/> — no container, part of the normal integration loop.
/// </summary>
public sealed class GameRoomListingScenario
{
    private const string Game = "demo-game";
    private const string TenantAKey = "dev-tenant-a-key"; // authenticated, no role, tenant-a
    private const string TenantBKey = "dev-tenant-b-key"; // authenticated, no role, tenant-b

    private readonly ITestOutputHelper _output;

    public GameRoomListingScenario(ITestOutputHelper output) => _output = output;

    private static HttpClient Client(WebApplicationFactory<Program> host, string key)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    // Local mirrors of the JSON the endpoint returns (camelCase; ReadFromJsonAsync is case-insensitive).
    private sealed record RoomSummary(
        string RoomId, string GameId, string TenantId, string Status, int SubscriberCount, long Tick, bool Live);
    private sealed record GameRooms(string GameId, IReadOnlyList<RoomSummary> Rooms);
    private sealed record RoomCreated(string RoomId, string TenantId, string GameId, string Status);

    [Fact]
    public async Task ListGameRooms_AlwaysReturnsAtLeastOneRoom_EvenWhenNoneWereCreated()
    {
        using var host = new WebApplicationFactory<Program>();
        var client = Client(host, TenantAKey);

        var resp = await client.GetAsync($"/api/v1/games/{Game}/rooms");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<GameRooms>();
        Assert.NotNull(body);
        Assert.Equal(Game, body!.GameId);
        Assert.NotEmpty(body.Rooms);                               // the "always >=1" guarantee
        Assert.All(body.Rooms, r => Assert.Equal(Game, r.GameId));
        Assert.All(body.Rooms, r => Assert.Equal("tenant-a", r.TenantId));
        Assert.All(body.Rooms, r => Assert.Equal("open", r.Status));
        _output.WriteLine($"auto-ensured rooms: {string.Join(",", body.Rooms.Select(r => r.RoomId))}");
    }

    [Fact]
    public async Task ACreatedRoom_AppearsInTheList_NotYetLive()
    {
        using var host = new WebApplicationFactory<Program>();
        var client = Client(host, TenantAKey);

        var created = await client.PostAsJsonAsync("/api/v1/rooms", new { TenantId = "tenant-a", GameId = Game });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var room = await created.Content.ReadFromJsonAsync<RoomCreated>();
        Assert.NotNull(room);

        var body = await (await client.GetAsync($"/api/v1/games/{Game}/rooms")).Content.ReadFromJsonAsync<GameRooms>();
        var match = Assert.Single(body!.Rooms, r => r.RoomId == room!.RoomId);
        // Created in the control plane but never joined -> no live realtime instance yet.
        Assert.False(match.Live);
        Assert.Equal(0, match.Tick);
        Assert.Equal(0, match.SubscriberCount);
    }

    [Fact]
    public async Task ListGameRooms_IsTenantScoped_TenantBCannotSeeTenantARooms()
    {
        using var host = new WebApplicationFactory<Program>();

        // tenant-a creates a room for the game.
        var aClient = Client(host, TenantAKey);
        var created = await aClient.PostAsJsonAsync("/api/v1/rooms", new { TenantId = "tenant-a", GameId = Game });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var aRoom = await created.Content.ReadFromJsonAsync<RoomCreated>();

        // tenant-b lists the same game: it sees ONLY its own (auto-ensured) rooms, never tenant-a's.
        var bClient = Client(host, TenantBKey);
        var bBody = await (await bClient.GetAsync($"/api/v1/games/{Game}/rooms")).Content.ReadFromJsonAsync<GameRooms>();
        Assert.NotEmpty(bBody!.Rooms);
        Assert.All(bBody.Rooms, r => Assert.Equal("tenant-b", r.TenantId));
        Assert.DoesNotContain(bBody.Rooms, r => r.RoomId == aRoom!.RoomId);
    }

    [Fact]
    public async Task ListGameRooms_UnknownGame_Returns404()
    {
        using var host = new WebApplicationFactory<Program>();
        var client = Client(host, TenantAKey);

        var resp = await client.GetAsync("/api/v1/games/no-such-game/rooms");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
