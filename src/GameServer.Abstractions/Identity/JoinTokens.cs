namespace GameServer.Identity;

/// <summary>
/// The trusted identity a verified join token grants a realtime connection: the
/// tenant/game/room/player it authorizes, plus its validity window. The realtime edge
/// treats these as authoritative — never the identity a client declares in its
/// handshake messages.
/// </summary>
public sealed record JoinTokenClaims(
    string TenantId,
    string GameId,
    string RoomId,
    string PlayerId,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc);

/// <summary>Why a token failed verification, or that it succeeded.</summary>
public enum TokenVerificationStatus
{
    Ok,
    Expired,
    BadSignature,
    Malformed,
}

/// <summary>
/// The outcome of verifying a token. <see cref="Claims"/> is populated only when
/// <see cref="Status"/> is <see cref="TokenVerificationStatus.Ok"/>.
/// </summary>
public readonly record struct TokenVerification(TokenVerificationStatus Status, JoinTokenClaims? Claims)
{
    public bool IsOk => Status == TokenVerificationStatus.Ok && Claims is not null;

    public static TokenVerification Ok(JoinTokenClaims claims) => new(TokenVerificationStatus.Ok, claims);

    public static TokenVerification Fail(TokenVerificationStatus status) => new(status, null);
}

/// <summary>Issues a signed, scoped, time-limited join token for a connection.</summary>
public interface IJoinTokenIssuer
{
    string Issue(string tenantId, string gameId, string roomId, string playerId);
}

/// <summary>Verifies a join token's signature and validity, returning its trusted claims.</summary>
public interface IJoinTokenVerifier
{
    TokenVerification Verify(string? token);
}
