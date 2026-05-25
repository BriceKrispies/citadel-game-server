using GameServer.Protocol;

namespace GameServer.Matchmaking;

/// <summary>
/// Matchmaking's port for "give me a room for a winning match". Defined HERE (not in Routing) on
/// purpose: matchmaking is rank 2 and Routing is also rank 2, so a direct dependency on Routing's
/// allocation API would be a sideways CITADEL0002 violation. The Host composition root adapts the real
/// <c>IRoomPlacement</c> (Routing) to this port. Keeping the seam here is also what gives matchmaking
/// ZERO dependency on the room runtime: it knows only "I get a room id back", never how a room is run.
/// </summary>
public interface IRoomAllocator
{
    /// <summary>
    /// Allocates (creating if needed) a room for a match in <paramref name="scope"/>. Returns the
    /// allocated room id, or null if no capacity could be obtained (the cluster is full) — a full
    /// cluster is a normal, non-exceptional outcome the director must shed cleanly.
    /// </summary>
    RoomId? Allocate(MatchScope scope);
}

/// <summary>
/// Matchmaking's port for "mint a join token for this matched player". Adapted at the Host to the real
/// Identity token issuer (<c>IJoinTokenIssuer</c> / <c>JoinTokenService</c>). Kept here so matchmaking
/// depends only on a token STRING it hands back to the player, not on the signing implementation.
/// </summary>
public interface IMatchJoinTokenIssuer
{
    /// <summary>Mints a signed, scoped join token authorizing <paramref name="player"/> into <paramref name="room"/>.</summary>
    string IssueJoinToken(MatchScope scope, RoomId room, PlayerId player);
}
