using GameServer.Protocol;
using GameServer.Simulation;
using GameServer.Tenancy;

namespace GameServer.Routing;

/// <summary>
/// Owns session lifecycle and room placement. Rooms are keyed by
/// <see cref="RoomKey"/> so tenant isolation is structural, not conditional.
/// </summary>
public interface ISessionRouter
{
    /// <summary>Creates a new session bound to the resolved tenant.</summary>
    Session CreateSession(TenantContext tenant);

    bool TryGetSession(SessionId sessionId, out Session session);

    /// <summary>
    /// Returns the room for (tenant, roomId), creating it on first placement with the
    /// game identified by <paramref name="gameId"/>. The returned room is shared by all
    /// players of that tenant's room.
    /// </summary>
    IGameRoom GetOrCreateRoom(TenantContext tenant, RoomId roomId, GameId gameId);

    /// <summary>Looks up an already-placed room by key, e.g. for the tick driver. Does not create.</summary>
    bool TryGetRoom(RoomKey key, out IGameRoom room);

    /// <summary>
    /// Removes a placed room (its lifecycle owner has decided it is no longer needed, e.g.
    /// the last player left). Returns true if a room was removed. A later join recreates it.
    /// </summary>
    bool TryRemoveRoom(RoomKey key);

    /// <summary>
    /// Atomically replaces the placed room for <paramref name="key"/> with <paramref name="room"/>,
    /// returning true if a room existed to replace. The swap seam for live rewind: a room rebuilt as
    /// of a past tick takes over the key in place. The caller is responsible for quiescing the room
    /// (holding its lock / pausing ticks) so no tick or command is mid-flight across the swap.
    /// </summary>
    bool TryReplaceRoom(RoomKey key, IGameRoom room);

    /// <summary>
    /// Keys of all currently placed rooms — the basis for bulk operations (e.g. rewinding every room
    /// of a tenant, or every room in the process). Filter by <see cref="RoomKey.TenantId"/> to stay
    /// within a tenant boundary.
    /// </summary>
    IReadOnlyCollection<RoomKey> RoomKeys { get; }
}
