using GameServer.Protocol;

namespace GameServer.Routing;

/// <summary>
/// The isolation-safe identity of a room: a room id is only meaningful within a
/// tenant. Two tenants using the same <see cref="RoomId"/> resolve to distinct
/// keys and therefore distinct, isolated room state.
/// </summary>
public readonly record struct RoomKey(TenantId TenantId, RoomId RoomId);
