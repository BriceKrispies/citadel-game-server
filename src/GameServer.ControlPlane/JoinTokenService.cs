using GameServer.Identity;

namespace GameServer.ControlPlane;

/// <summary>
/// Control-plane facade for minting join tokens. It produces the API
/// <see cref="JoinTokenContract"/> and delegates signing to an
/// <see cref="IJoinTokenIssuer"/>. Tokens are now self-contained and signed, so the
/// realtime edge verifies them statelessly (via <see cref="IJoinTokenVerifier"/>) —
/// there is no server-side token store to share across instances.
/// </summary>
public sealed class JoinTokenService
{
    private readonly IJoinTokenIssuer _issuer;

    public JoinTokenService(IJoinTokenIssuer issuer) => _issuer = issuer;

    public JoinTokenContract Issue(string tenantId, string gameId, string roomId, string playerId)
    {
        var token = _issuer.Issue(tenantId, gameId, roomId, playerId);
        return new JoinTokenContract(token, tenantId, gameId, roomId, playerId);
    }
}
