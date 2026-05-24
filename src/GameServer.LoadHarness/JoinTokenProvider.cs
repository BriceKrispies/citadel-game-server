using System.Net.Http.Json;

namespace GameServer.LoadHarness;

/// <summary>
/// Obtains realtime join tokens. In <see cref="JoinTokenMode.ControlPlane"/> it
/// creates a room once via the HTTP control plane and issues a token per client;
/// in <see cref="JoinTokenMode.Static"/> it returns the configured token (custom
/// setups). Tokens authorize the WebSocket upgrade; clients still join their
/// strategy-assigned room via ClientJoinRoom.
/// </summary>
public sealed class JoinTokenProvider
{
    private readonly HttpClient _http;
    private readonly ScenarioConfig _config;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private string? _roomId;

    public JoinTokenProvider(HttpClient http, ScenarioConfig config)
    {
        _http = http;
        _config = config;
    }

    public async Task<string> GetTokenAsync(string playerId, CancellationToken cancellationToken)
    {
        if (_config.JoinTokenMode == JoinTokenMode.Static)
        {
            return _config.JoinToken ?? throw new InvalidOperationException("Static joinTokenMode requires a joinToken.");
        }

        var roomId = await EnsureRoomAsync(cancellationToken).ConfigureAwait(false);
        var response = await _http.PostAsJsonAsync(
            $"{_config.ControlPlaneBaseUrl}/api/v1/rooms/{roomId}/join-token",
            new { playerId },
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken).ConfigureAwait(false);
        return token?.Token ?? throw new InvalidOperationException("Join-token response had no token.");
    }

    private async Task<string> EnsureRoomAsync(CancellationToken cancellationToken)
    {
        if (_roomId is not null)
        {
            return _roomId;
        }

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_roomId is null)
            {
                var response = await _http.PostAsJsonAsync(
                    $"{_config.ControlPlaneBaseUrl}/api/v1/rooms",
                    new { tenantId = _config.TenantId, gameId = _config.GameId },
                    cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var room = await response.Content.ReadFromJsonAsync<RoomResponse>(cancellationToken).ConfigureAwait(false);
                _roomId = room?.RoomId ?? throw new InvalidOperationException("Create-room response had no roomId.");
            }
        }
        finally
        {
            _initLock.Release();
        }

        return _roomId;
    }

    private sealed record TokenResponse(string Token);

    private sealed record RoomResponse(string RoomId);
}
