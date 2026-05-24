using System.Collections.Concurrent;

namespace GameServer.ControlPlane;

/// <summary>
/// Control-plane room registry. Creating a room here records room metadata only —
/// it does NOT create or run an authoritative simulation room. The simulation room
/// is created lazily in the realtime data plane when a player joins. Room ids are
/// issued from a deterministic counter (no clock/RNG).
/// </summary>
public sealed class InMemoryRoomRegistry
{
    private readonly ConcurrentDictionary<string, RoomContract> _rooms = new();
    private long _counter;

    public RoomContract Create(CreateRoomRequest request)
    {
        var roomId = $"room-{System.Threading.Interlocked.Increment(ref _counter)}";
        var room = new RoomContract(roomId, request.TenantId, request.GameId, Status: "open");
        _rooms[roomId] = room;
        return room;
    }

    public bool TryGet(string roomId, out RoomContract room) => _rooms.TryGetValue(roomId, out room!);
}
