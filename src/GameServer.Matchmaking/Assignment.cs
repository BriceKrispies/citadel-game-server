using GameServer.Protocol;

namespace GameServer.Matchmaking;

/// <summary>
/// The result of assigning one matched player to a room: the room it was placed in and the join token
/// it presents to the realtime edge. This is what flows back to the client — the room runtime is never
/// touched by matchmaking; the player connects to it later with this token.
/// </summary>
public sealed record PlayerAssignment(
    string TicketId,
    PlayerId PlayerId,
    MatchScope Scope,
    RoomId RoomId,
    string JoinToken);

/// <summary>One winning match turned into a room plus a per-player assignment.</summary>
public sealed record MatchAssignment(
    MatchScope Scope,
    RoomId RoomId,
    IReadOnlyList<PlayerAssignment> Players);
