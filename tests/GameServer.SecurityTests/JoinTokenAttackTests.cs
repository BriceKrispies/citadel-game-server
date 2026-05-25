using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameServer.Identity;

namespace GameServer.SecurityTests;

/// <summary>
/// Join-token forgery matrix against the realtime edge (confirmed finding #3, the dev-secret
/// fallback). The edge verifies HS256 statelessly; a token NOT signed with the container's real
/// secret — forged, tampered, expired, or alg-swapped — must be rejected before the WebSocket
/// upgrade. A rejected token means the upgrade never completes, so <c>ConnectAsync</c> throws.
/// </summary>
public sealed class JoinTokenAttackTests
{
    private static readonly SecurityTarget Target = SecurityTarget.Current;

    private static CancellationToken Ct => new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token;

    private const string WrongSecret = "totally-wrong-attacker-secret-0123456789-pad";

    /// <summary>Asserts the realtime upgrade is REJECTED for the given (bad) token.</summary>
    private static async Task AssertRejectedAsync(string token)
    {
        // A rejected token makes the server answer 401 before the 101 upgrade, so the WS
        // handshake fails. We must NOT observe an open socket.
        await Assert.ThrowsAnyAsync<WebSocketException>(async () =>
        {
            await using var client = await RealtimeWireClient.ConnectWithTokenAsync(Target, token, Ct);
        });
    }

    [SkippableFact]
    public async Task ForgedSignature_WrongSecret_IsRejected()
    {
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);

        // A perfectly-shaped token signed with a secret the container does not hold.
        var forger = new Hs256JoinTokenCodec(WrongSecret, new FixedClock(DateTimeOffset.UtcNow), TimeSpan.FromMinutes(5));
        var token = forger.Issue(SecurityTarget.TenantA, ControlPlane.GameId, "any-room", "intruder");

        await AssertRejectedAsync(token);
    }

    [SkippableFact]
    public async Task TamperedPayload_IsRejected()
    {
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);

        // Mint a VALID token with the real secret, then flip the payload (escalate the player)
        // while leaving the original signature in place.
        var real = new Hs256JoinTokenCodec(Target.JoinSecret!, new FixedClock(DateTimeOffset.UtcNow), TimeSpan.FromMinutes(5));
        var token = real.Issue(SecurityTarget.TenantA, ControlPlane.GameId, "any-room", "player-1");

        var parts = token.Split('.');
        var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
        var tampered = payloadJson.Replace("player-1", "admin-9");
        parts[1] = Base64UrlEncode(Encoding.UTF8.GetBytes(tampered));

        await AssertRejectedAsync(string.Join('.', parts));
    }

    [SkippableFact]
    public async Task ExpiredToken_IsRejected()
    {
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);

        // Sign with the REAL secret but stamp it an hour in the past so it is already expired.
        var pastClock = new FixedClock(DateTimeOffset.UtcNow.AddHours(-1));
        var codec = new Hs256JoinTokenCodec(Target.JoinSecret!, pastClock, TimeSpan.FromMinutes(2));
        var token = codec.Issue(SecurityTarget.TenantA, ControlPlane.GameId, "any-room", "player-1");

        await AssertRejectedAsync(token);
    }

    [SkippableFact]
    public async Task AlgNone_IsRejected()
    {
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);

        // Classic "alg=none" downgrade: well-formed header/payload, empty signature segment.
        var header = Base64UrlEncode(Encoding.UTF8.GetBytes("{\"alg\":\"none\",\"typ\":\"JWT\"}"));
        var payload = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            tid = SecurityTarget.TenantA,
            gid = ControlPlane.GameId,
            rid = "any-room",
            pid = "intruder",
            iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
        })));

        await AssertRejectedAsync($"{header}.{payload}.");
    }

    [SkippableFact]
    public async Task AlgSwap_Hs256SignedWithWrongKeyAndAlteredHeader_IsRejected()
    {
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);

        // Swap the declared alg in the header while signing with a non-container key. The edge
        // signs the EXACT header+payload bytes with its own secret, so the presented signature
        // (over a different header / different key) can never match.
        var header = Base64UrlEncode(Encoding.UTF8.GetBytes("{\"alg\":\"HS512\",\"typ\":\"JWT\"}"));
        var payload = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            tid = SecurityTarget.TenantA,
            gid = ControlPlane.GameId,
            rid = "any-room",
            pid = "intruder",
            iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
        })));
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(WrongSecret));
        var sig = Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{header}.{payload}")));

        await AssertRejectedAsync($"{header}.{payload}.{sig}");
    }

    [SkippableFact]
    public async Task MissingToken_IsRejected()
    {
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);
        await AssertRejectedAsync(string.Empty);
    }

    [SkippableFact]
    public async Task GarbageToken_IsRejected()
    {
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);
        await AssertRejectedAsync("not.a.jwt");
    }

    [SkippableFact]
    public async Task ValidToken_IsAccepted()
    {
        // Positive control: a token minted by the container's own control plane DOES upgrade,
        // proving the rejections above are about the signature/validity, not a broken endpoint.
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);

        var (_, _, token) = await ControlPlane.ProvisionJoinableRoomAsync(Target, Target.TenantAKey!, SecurityTarget.TenantA, Ct);
        await using var client = await RealtimeWireClient.ConnectWithTokenAsync(Target, token, Ct);
        Assert.Equal(WebSocketState.Open, client.State);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
        return Convert.FromBase64String(padded);
    }
}
