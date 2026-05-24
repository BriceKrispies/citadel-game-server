using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameServer.Identity;

/// <summary>
/// Issues and verifies join tokens as compact HS256 (HMAC-SHA256) JWTs:
/// <c>base64url(header).base64url(payload).base64url(signature)</c>. The same shared
/// secret signs and verifies, which is the right model when one trust domain (this
/// platform's control plane) issues and its own realtime edge verifies — and it
/// verifies statelessly, so any instance can validate a token without shared state.
/// </summary>
/// <remarks>
/// Implements both the issuer and verifier seams. Swapping to asymmetric RS256/JWKS
/// for an external identity provider is a replacement of this one component behind
/// <see cref="IJoinTokenIssuer"/>/<see cref="IJoinTokenVerifier"/> — no caller changes.
/// </remarks>
public sealed class Hs256JoinTokenCodec : IJoinTokenIssuer, IJoinTokenVerifier
{
    private const string HeaderJson = "{\"alg\":\"HS256\",\"typ\":\"JWT\"}";

    private readonly byte[] _secret;
    private readonly IClock _clock;
    private readonly TimeSpan _tokenLifetime;

    public Hs256JoinTokenCodec(string secret, IClock clock, TimeSpan tokenLifetime)
    {
        if (string.IsNullOrEmpty(secret))
        {
            throw new ArgumentException("A signing secret is required.", nameof(secret));
        }

        if (tokenLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(tokenLifetime), tokenLifetime, "Token lifetime must be positive.");
        }

        _secret = Encoding.UTF8.GetBytes(secret);
        _clock = clock;
        _tokenLifetime = tokenLifetime;
    }

    public string Issue(string tenantId, string gameId, string roomId, string playerId)
    {
        var issuedAt = _clock.UtcNow;
        var payload = new TokenPayload(
            tenantId, gameId, roomId, playerId,
            issuedAt.ToUnixTimeSeconds(),
            issuedAt.Add(_tokenLifetime).ToUnixTimeSeconds());

        var signingInput =
            Base64UrlEncode(Encoding.UTF8.GetBytes(HeaderJson)) + "." +
            Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload));

        return signingInput + "." + Base64UrlEncode(Sign(signingInput));
    }

    public TokenVerification Verify(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return TokenVerification.Fail(TokenVerificationStatus.Malformed);
        }

        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return TokenVerification.Fail(TokenVerificationStatus.Malformed);
        }

        string payloadJson;
        try
        {
            // Verify the signature before trusting any byte of the payload.
            var presented = Base64UrlDecode(parts[2]);
            var expected = Sign(parts[0] + "." + parts[1]);
            if (!CryptographicOperations.FixedTimeEquals(presented, expected))
            {
                return TokenVerification.Fail(TokenVerificationStatus.BadSignature);
            }

            payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
        }
        catch (FormatException)
        {
            return TokenVerification.Fail(TokenVerificationStatus.Malformed);
        }

        TokenPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<TokenPayload>(payloadJson);
        }
        catch (JsonException)
        {
            return TokenVerification.Fail(TokenVerificationStatus.Malformed);
        }

        if (payload is null)
        {
            return TokenVerification.Fail(TokenVerificationStatus.Malformed);
        }

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(payload.Exp);
        if (_clock.UtcNow > expiresAt)
        {
            return TokenVerification.Fail(TokenVerificationStatus.Expired);
        }

        return TokenVerification.Ok(new JoinTokenClaims(
            payload.Tid, payload.Gid, payload.Rid, payload.Pid,
            DateTimeOffset.FromUnixTimeSeconds(payload.Iat), expiresAt));
    }

    private byte[] Sign(string signingInput)
    {
        using var hmac = new HMACSHA256(_secret);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput));
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
        return Convert.FromBase64String(padded);
    }

    // Wire-level claim shape (compact keys). Internal so System.Text.Json can bind it;
    // never exposed — callers see JoinTokenClaims.
    internal sealed record TokenPayload(string Tid, string Gid, string Rid, string Pid, long Iat, long Exp);
}
