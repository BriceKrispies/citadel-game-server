namespace GameServer.Persistence.Postgres;

/// <summary>
/// The tenant-scoped durable identity of a keyed stream, derived from a caller's opaque key by a
/// selector supplied at the composition root. The Postgres adapter is generic over the caller's key
/// type (so it carries no dependency on the rank-2 <c>RoomKey</c>); the selector projects that key
/// into the two parts the adapter needs:
/// <list type="bullet">
///   <item><see cref="TenantId"/> — routes to the tenant's own database (the isolation boundary).</item>
///   <item><see cref="RoomId"/> — the row identity WITHIN that tenant's database.</item>
/// </list>
/// </summary>
public readonly struct DurableRoomKey : IEquatable<DurableRoomKey>
{
    public DurableRoomKey(string tenantId, string roomId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new ArgumentException("A tenant id is required for tenant-scoped persistence.", nameof(tenantId));
        }

        if (string.IsNullOrWhiteSpace(roomId))
        {
            throw new ArgumentException("A room id is required.", nameof(roomId));
        }

        TenantId = tenantId;
        RoomId = roomId;
    }

    public string TenantId { get; }

    public string RoomId { get; }

    public bool Equals(DurableRoomKey other) => TenantId == other.TenantId && RoomId == other.RoomId;

    public override bool Equals(object? obj) => obj is DurableRoomKey other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(TenantId, RoomId);
}
