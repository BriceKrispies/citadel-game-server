using System.Text.Json;

namespace GameServer.LoadHarness;

/// <summary>The server's authoritative view of one active room (from <c>GET /api/v1/admin/rooms</c>).</summary>
public sealed record ObservedRoom(string TenantId, string RoomId, long Tick, int SubscriberCount, int EntityCount);

/// <summary>
/// Cross-checks the harness's client-side receive tallies against the server's own truth.
/// Polls the read-only admin API for active rooms and reports, per room, the authoritative
/// subscriber count and current tick. "Each room is receiving updates" holds when every
/// expected room reports the expected subscriber count and a tick that advances between
/// two polls — proof from the server, not just from the clients.
///
/// Auth: the call carries the configured <see cref="ScenarioConfig.ApiKey"/> (set as a
/// default header on the shared <see cref="HttpClient"/>); a per-tenant key suffices, as
/// the admin endpoint returns only rooms the caller may act for.
/// </summary>
public sealed class ServerRoomVerifier
{
    private readonly HttpClient _http;
    private readonly ScenarioConfig _config;

    public ServerRoomVerifier(HttpClient http, ScenarioConfig config)
    {
        _http = http;
        _config = config;
    }

    /// <summary>Fetches the server's current view of this tenant's active rooms.</summary>
    public async Task<IReadOnlyList<ObservedRoom>> ObserveRoomsAsync(CancellationToken cancellationToken)
    {
        using var response = await _http
            .GetAsync($"{_config.ControlPlaneBaseUrl}/api/v1/admin/rooms", cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Parse(json, _config.TenantId);
    }

    /// <summary>Parses the admin-rooms response, keeping only rooms in <paramref name="tenantId"/>.</summary>
    public static IReadOnlyList<ObservedRoom> Parse(string json, string tenantId)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("rooms", out var rooms) || rooms.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ObservedRoom>();
        }

        var result = new List<ObservedRoom>();
        foreach (var room in rooms.EnumerateArray())
        {
            var roomTenant = GetString(room, "tenantId");
            if (!string.Equals(roomTenant, tenantId, StringComparison.Ordinal))
            {
                continue;
            }

            result.Add(new ObservedRoom(
                roomTenant,
                GetString(room, "roomId"),
                GetLong(room, "tick"),
                (int)GetLong(room, "subscriberCount"),
                (int)GetLong(room, "entityCount")));
        }

        return result;
    }

    private static string GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : string.Empty;

    private static long GetLong(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
}
