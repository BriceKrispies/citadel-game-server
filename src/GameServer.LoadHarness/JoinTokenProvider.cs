using System.Net.Http.Json;

namespace GameServer.LoadHarness;

/// <summary>
/// Obtains realtime join tokens. In <see cref="JoinTokenMode.ControlPlane"/> it first
/// provisions the scenario's rooms via the HTTP control plane (capturing the real room
/// ids the registry assigns), then issues a token per client scoped to that client's
/// assigned room. The realtime server rejects a <c>ClientJoinRoom</c> whose room id does
/// not match the token's claim, so the token MUST be minted for the exact room the client
/// will join — that is why this issues per-room, not one shared token.
///
/// In <see cref="JoinTokenMode.Static"/> it returns the configured token for every client
/// (custom setups) and does not contact the control plane.
/// </summary>
public sealed class JoinTokenProvider
{
    private readonly HttpClient _http;
    private readonly ScenarioConfig _config;

    public JoinTokenProvider(HttpClient http, ScenarioConfig config)
    {
        _http = http;
        _config = config;
    }

    /// <summary>
    /// Creates <paramref name="count"/> rooms in the control plane and returns the room ids
    /// the registry assigned. In static mode no rooms are created and synthesized ids are
    /// returned (the static token governs access).
    /// </summary>
    public async Task<IReadOnlyList<string>> ProvisionRoomsAsync(int count, CancellationToken cancellationToken)
    {
        if (count < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "Room count must be >= 1.");
        }

        if (_config.JoinTokenMode == JoinTokenMode.Static)
        {
            return Enumerable.Range(1, count).Select(n => $"room-{n}").ToArray();
        }

        var rooms = new string[count];
        for (var i = 0; i < count; i++)
        {
            rooms[i] = await CreateRoomAsync(cancellationToken).ConfigureAwait(false);
        }

        return rooms;
    }

    /// <summary>Issues (or returns) a token authorizing <paramref name="playerId"/> to join <paramref name="roomId"/>.</summary>
    public async Task<string> GetTokenAsync(string playerId, string roomId, CancellationToken cancellationToken)
    {
        if (_config.JoinTokenMode == JoinTokenMode.Static)
        {
            return _config.JoinToken ?? throw new InvalidOperationException("Static joinTokenMode requires a joinToken.");
        }

        var response = await _http.PostAsJsonAsync(
            $"{_config.ControlPlaneBaseUrl}/api/v1/rooms/{roomId}/join-token",
            new { playerId },
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken).ConfigureAwait(false);
        return token?.Token ?? throw new InvalidOperationException("Join-token response had no token.");
    }

    private async Task<string> CreateRoomAsync(CancellationToken cancellationToken)
    {
        var response = await _http.PostAsJsonAsync(
            $"{_config.ControlPlaneBaseUrl}/api/v1/rooms",
            new { tenantId = _config.TenantId, gameId = _config.GameId },
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var room = await response.Content.ReadFromJsonAsync<RoomResponse>(cancellationToken).ConfigureAwait(false);
        return room?.RoomId ?? throw new InvalidOperationException("Create-room response had no roomId.");
    }

    private sealed record TokenResponse(string Token);

    private sealed record RoomResponse(string RoomId);
}
