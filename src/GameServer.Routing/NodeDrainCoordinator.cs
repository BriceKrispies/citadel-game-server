namespace GameServer.Routing;

/// <summary>
/// The outcome of shedding one room from a draining node: where it was re-placed (or that the cluster
/// had no live capacity to take it). Returned per-room so a drain reports exactly what moved and what
/// could not, rather than silently dropping a room.
/// </summary>
/// <param name="Room">The room being shed.</param>
/// <param name="Result">The re-placement result (the new owner, or cluster-at-capacity).</param>
public readonly record struct RoomShedOutcome(RoomKey Room, RoomPlacementResult Result);

/// <summary>
/// Drains a node cleanly: marks it draining (so it accepts no new allocations — fleet-visible via the
/// directory), then sheds each room it owns by re-placing the room on a live node and releasing the old
/// claim only after the new owner is secured. Ordering matters: re-place THEN release, so a room is never
/// owner-less (stranded) mid-migration, and never double-owned (the directory fences the claim). A room
/// the cluster cannot re-home (no live capacity) is left owned by the draining node and reported, rather
/// than released into the void — correctness over completeness.
/// </summary>
/// <remarks>
/// This is the placement/ownership half of a drain. Migrating the live in-memory room STATE is the
/// composition root's job (it owns <c>RealtimeServer</c>); this coordinator owns the directory bookkeeping
/// that decides where each room must live next, and is fully testable without a host.
/// </remarks>
public sealed class NodeDrainCoordinator
{
    private readonly IRoomDirectory _directory;
    private readonly IRoomPlacement _placement;

    public NodeDrainCoordinator(IRoomDirectory directory, IRoomPlacement placement)
    {
        _directory = directory;
        _placement = placement;
    }

    /// <summary>
    /// Marks <paramref name="node"/> draining and sheds every room it owns. Returns one outcome per room
    /// shed (in directory enumeration order). After this returns, the node owns only rooms the cluster had
    /// no capacity to take (each reported with <see cref="RoomPlacementStatus.ClusterAtCapacity"/>).
    /// </summary>
    public IReadOnlyList<RoomShedOutcome> Drain(NodeId node)
    {
        // Mark draining FIRST so placement (which we are about to call per room) will never re-home a room
        // back onto this same node, and so no new allocation lands here while we shed.
        _directory.SetNodeDraining(node, true);

        var outcomes = new List<RoomShedOutcome>();
        foreach (var room in _directory.OwnedRooms(node))
        {
            // Re-place under the SAME placement clients use. Placement treats a draining owner as
            // non-sticky, so it atomically moves the room off this node onto a live one (or, if the cluster
            // has no live capacity, leaves the room here and reports ClusterAtCapacity). The move is one
            // atomic operation (a lock for in-memory, a single Lua script for Redis) — there is no window in
            // which the room is owner-less (stranded) or owned by two nodes (split brain).
            outcomes.Add(new RoomShedOutcome(room, _placement.Place(room)));
        }

        return outcomes;
    }
}
