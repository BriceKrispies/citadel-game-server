using GameServer.ControlPlane;
using GameServer.Matchmaking;
using GameServer.Protocol;
using GameServer.Routing;

namespace GameServer.Host;

/// <summary>
/// Composition-root adapter wiring matchmaking's <see cref="IRoomAllocator"/> port to the REAL Wave-4
/// allocation path (control-plane room registry + <see cref="IRoomPlacement"/> from
/// <c>GameServer.Routing</c>). This adapter is the only place Matchmaking (rank 2) and Routing (rank 2)
/// meet — and they meet HERE, in the Host (a layer-exempt composition root), never via a direct
/// project reference. That is what keeps the rank-2↔rank-2 sideways dependency out of the build
/// (CITADEL0002) while still letting a matched player be placed on a real owning node.
/// </summary>
public sealed class RoutingRoomAllocator : IRoomAllocator
{
    private readonly InMemoryRoomRegistry _rooms;
    private readonly IRoomPlacement _placement;

    public RoutingRoomAllocator(InMemoryRoomRegistry rooms, IRoomPlacement placement)
    {
        _rooms = rooms;
        _placement = placement;
    }

    public RoomId? Allocate(MatchScope scope)
    {
        // Create control-plane room metadata, then place it on an owning node (capacity-aware). A full
        // cluster returns null so the director sheds the match cleanly rather than placing a roomless one.
        var room = _rooms.Create(new CreateRoomRequest(scope.TenantId.Value, scope.GameId.Value));
        var placed = _placement.Place(new RoomKey(scope.TenantId, new RoomId(room.RoomId)));
        return placed.IsPlaced ? new RoomId(room.RoomId) : null;
    }
}

/// <summary>
/// Composition-root adapter wiring matchmaking's <see cref="IMatchJoinTokenIssuer"/> port to the REAL
/// control-plane <see cref="JoinTokenService"/> (which signs via Identity's <c>IJoinTokenIssuer</c>).
/// The token carries the matched player's tenant/game/room/player scope, so the realtime edge verifies
/// and trusts it exactly as it does a hand-minted join token — matchmaking never sees the signing key.
/// </summary>
public sealed class JoinTokenServiceIssuer : IMatchJoinTokenIssuer
{
    private readonly JoinTokenService _tokens;

    public JoinTokenServiceIssuer(JoinTokenService tokens) => _tokens = tokens;

    public string IssueJoinToken(MatchScope scope, RoomId room, PlayerId player) =>
        _tokens.Issue(scope.TenantId.Value, scope.GameId.Value, room.Value, player.Value).Token;
}
