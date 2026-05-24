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
}
