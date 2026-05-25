using System.Collections.Concurrent;
using System.Linq;

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

    // Serializes EnsureAtLeastOne for a given tenant+game so two concurrent ensures cannot both create
    // (the room-discovery contract is "always exactly-at-least one", not "race two into existence"). The
    // hot Create path stays lock-free; only the auto-ensure read-then-maybe-create takes this gate.
    private readonly object _ensureGate = new();
    private long _counter;

    public RoomContract Create(CreateRoomRequest request)
    {
        var roomId = $"room-{System.Threading.Interlocked.Increment(ref _counter)}";
        var room = new RoomContract(roomId, request.TenantId, request.GameId, Status: "open");
        _rooms[roomId] = room;
        return room;
    }

    public bool TryGet(string roomId, out RoomContract room) => _rooms.TryGetValue(roomId, out room!);

    /// <summary>
    /// The OPEN rooms for one tenant+game. Strictly tenant-scoped: a caller listing its own tenant's
    /// rooms can never observe another tenant's rooms (room discovery must not leak across tenants).
    /// </summary>
    public IReadOnlyList<RoomContract> ListForGame(string tenantId, string gameId) =>
        _rooms.Values
            .Where(r => r.TenantId == tenantId && r.GameId == gameId && r.Status == "open")
            .ToList();

    /// <summary>
    /// Guarantees the tenant has at least one open room for the game, returning a representative one:
    /// if none exists it creates a single default room, otherwise it returns an existing one. Idempotent
    /// under <see cref="_ensureGate"/> (concurrent callers create at most one), and only ever creates a
    /// room within <paramref name="tenantId"/> — never cross-tenant.
    /// </summary>
    public RoomContract EnsureAtLeastOne(string tenantId, string gameId)
    {
        lock (_ensureGate)
        {
            var existing = ListForGame(tenantId, gameId);
            return existing.Count > 0 ? existing[0] : Create(new CreateRoomRequest(tenantId, gameId));
        }
    }
}
