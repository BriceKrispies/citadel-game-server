namespace GameServer.Transport;

/// <summary>
/// One entity in a room observation. Carries the generic relevance key (so an admin tool
/// can plot position for ANY game without decoding the opaque payload) plus the raw
/// game-defined payload (base64) and version, for tools that do understand the game.
/// </summary>
public sealed record ObservedEntity(string EntityId, long Version, double X, double Y, string Group, string PayloadBase64);

/// <summary>
/// One connection viewing a room: its identity and lag. <see cref="PendingSnapshots"/> is
/// the unacknowledged backlog (0 = caught up; rising = the client has stopped acking).
/// </summary>
public sealed record ObservedViewer(string ConnectionId, string? PlayerId, int PendingSnapshots);

/// <summary>
/// A read-only snapshot of a live room for operator/admin debugging: the authoritative
/// tick, every projected entity, and every connected viewer with its lag. This is the
/// "drop in and see what's happening" view; it never mutates room state.
/// </summary>
public sealed record RoomObservation(
    string TenantId,
    string RoomId,
    long Tick,
    IReadOnlyList<ObservedEntity> Entities,
    IReadOnlyList<ObservedViewer> Viewers)
{
    public int SubscriberCount => Viewers.Count;
}
