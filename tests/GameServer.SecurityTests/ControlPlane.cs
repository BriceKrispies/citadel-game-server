using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace GameServer.SecurityTests;

/// <summary>
/// Thin black-box wrapper over the container's control-plane HTTP API: create a room, mint a join
/// token. Used by positive controls (get a real token, then connect over WS). Mirrors the
/// DTO shapes in <c>GameServer.ControlPlane.ApiContracts</c> without referencing that layer.
/// </summary>
public static class ControlPlane
{
    /// <summary>A game id seeded in the host's in-memory catalog (room creation 404s for an
    /// unknown game). The catalog seeds "demo-game" / "demo-delta" / "demo-budget".</summary>
    public const string GameId = "demo-game";

    private sealed record CreateRoomBody(string TenantId, string GameId);
    private sealed record CreateJoinTokenBody(string PlayerId);

    public sealed record RoomResult(string RoomId, string TenantId, string GameId);
    public sealed record JoinTokenResult([property: JsonPropertyName("token")] string Token);

    /// <summary>
    /// Creates a room for <paramref name="tenantId"/> using <paramref name="apiKey"/> and returns
    /// its server-assigned id. Throws if the control plane rejects the request.
    /// </summary>
    public static async Task<RoomResult> CreateRoomAsync(SecurityTarget target, string apiKey, string tenantId, CancellationToken ct)
    {
        using var client = target.NewHttpClient(apiKey);
        var resp = await client.PostAsJsonAsync("/api/v1/rooms", new CreateRoomBody(tenantId, GameId), ct);
        resp.EnsureSuccessStatusCode();
        var room = await resp.Content.ReadFromJsonAsync<RoomResult>(ct);
        return room ?? throw new InvalidOperationException("Room creation returned an empty body.");
    }

    /// <summary>Mints a join token for an existing room. Throws on a non-2xx response.</summary>
    public static async Task<string> MintJoinTokenAsync(
        SecurityTarget target, string apiKey, string roomId, string playerId, CancellationToken ct)
    {
        using var client = target.NewHttpClient(apiKey);
        var resp = await client.PostAsJsonAsync($"/api/v1/rooms/{roomId}/join-token", new CreateJoinTokenBody(playerId), ct);
        resp.EnsureSuccessStatusCode();
        var token = await resp.Content.ReadFromJsonAsync<JoinTokenResult>(ct);
        return token?.Token ?? throw new InvalidOperationException("Join-token mint returned an empty body.");
    }

    /// <summary>
    /// Creates a room as <paramref name="tenantId"/> and returns a valid join token for a fresh
    /// player — the common positive-control setup for a realtime connection.
    /// </summary>
    public static async Task<(string RoomId, string PlayerId, string Token)> ProvisionJoinableRoomAsync(
        SecurityTarget target, string apiKey, string tenantId, CancellationToken ct)
    {
        var room = await CreateRoomAsync(target, apiKey, tenantId, ct);
        var playerId = "p-" + Guid.NewGuid().ToString("n")[..8];
        var token = await MintJoinTokenAsync(target, apiKey, room.RoomId, playerId, ct);
        return (room.RoomId, playerId, token);
    }
}
