using System.Collections.Concurrent;
using GameServer.Protocol;
using GameServer.Simulation;
using GameServer.Tenancy;

namespace GameServer.Routing;

/// <summary>
/// In-memory session router and room registry. Session ids are issued from a
/// monotonic counter (not a clock or RNG) so routing stays deterministic under
/// test. Rooms are built lazily via the injected <see cref="GameRoomFactory"/>,
/// which is where the room's deterministic clock/random are bound.
/// </summary>
public sealed class InMemorySessionRouter : ISessionRouter
{
    private readonly GameRoomFactory _roomFactory;
    private readonly ConcurrentDictionary<SessionId, Session> _sessions = new();
    private readonly ConcurrentDictionary<RoomKey, IGameRoom> _rooms = new();
    private long _sessionCounter;

    public InMemorySessionRouter(GameRoomFactory roomFactory) => _roomFactory = roomFactory;

    public Session CreateSession(TenantContext tenant)
    {
        var n = Interlocked.Increment(ref _sessionCounter);
        var session = new Session(new SessionId($"session-{n}"), tenant);
        _sessions[session.Id] = session;
        return session;
    }

    public bool TryGetSession(SessionId sessionId, out Session session) =>
        _sessions.TryGetValue(sessionId, out session!);

    public IGameRoom GetOrCreateRoom(TenantContext tenant, RoomId roomId, GameId gameId)
    {
        var key = new RoomKey(tenant.TenantId, roomId);
        return _rooms.GetOrAdd(key, _ => _roomFactory(roomId, gameId));
    }

    public bool TryGetRoom(RoomKey key, out IGameRoom room) => _rooms.TryGetValue(key, out room!);

    public bool TryRemoveRoom(RoomKey key) => _rooms.TryRemove(key, out _);

    /// <summary>Number of placed rooms. A leak indicator: it should fall as rooms empty out.</summary>
    public int RoomCount => _rooms.Count;

    /// <summary>Keys of all currently placed rooms (for diagnostics/scenarios).</summary>
    public IReadOnlyCollection<RoomKey> RoomKeys => _rooms.Keys.ToArray();
}
