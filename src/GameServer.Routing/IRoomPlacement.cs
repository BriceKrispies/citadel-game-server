namespace GameServer.Routing;

/// <summary>Whether a room was placed on a node, or every node was already at capacity.</summary>
public enum RoomPlacementStatus
{
    /// <summary>The room was assigned to an owning node (see <see cref="RoomPlacementResult.Owner"/>).</summary>
    Placed,

    /// <summary>No node had spare room headroom; the room was not placed.</summary>
    ClusterAtCapacity,
}

/// <summary>
/// The outcome of a placement attempt: the owning node when <see cref="Status"/> is
/// <see cref="RoomPlacementStatus.Placed"/>, or a signal that the cluster is full. Modelled as a
/// result rather than a bare <see cref="NodeId"/> so "no capacity anywhere" is an explicit, testable
/// outcome instead of an exception or a sentinel node.
/// </summary>
public readonly record struct RoomPlacementResult(RoomPlacementStatus Status, NodeId Owner)
{
    /// <summary>The room was placed on <paramref name="owner"/>.</summary>
    public static RoomPlacementResult OnNode(NodeId owner) => new(RoomPlacementStatus.Placed, owner);

    /// <summary>No node had spare capacity; the room was not placed.</summary>
    public static RoomPlacementResult ClusterAtCapacity { get; } = new(RoomPlacementStatus.ClusterAtCapacity, default);

    /// <summary>True when the room was placed on a node.</summary>
    public bool IsPlaced => Status == RoomPlacementStatus.Placed;
}

/// <summary>
/// Assigns a room to exactly one owning node across the cluster. Placement is idempotent — the same
/// <see cref="RoomKey"/> resolves to the same node until it is released — and capacity-aware: a room
/// is placed on a node that still has headroom, spilling to another node once one is at its room cap
/// and reporting <see cref="RoomPlacementStatus.ClusterAtCapacity"/> once every node is full. A
/// successful placement is recorded in the <see cref="IRoomDirectory"/>; affinity routing reads it.
/// </summary>
public interface IRoomPlacement
{
    /// <summary>Returns the placement for <paramref name="room"/>, assigning an owner on first placement.</summary>
    RoomPlacementResult Place(RoomKey room);
}
