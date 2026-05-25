namespace GameServer.Routing;

/// <summary>
/// Places a room on the first configured node with spare capacity and records the assignment in the
/// <see cref="IRoomDirectory"/>. Placement is idempotent — an already-owned room returns its existing
/// owner — and capacity-aware: it skips nodes already at their allocatable ceiling and reports
/// <see cref="RoomPlacementStatus.ClusterAtCapacity"/> when none have room.
/// </summary>
/// <remarks>
/// <para>
/// Headroom: the hard ceiling is <c>maxRoomsPerNode</c>, but placement only fills a node up to
/// <c>maxRoomsPerNode - reservedHeadroom</c>. The reserved slots are kept free so a node always has spare
/// capacity to absorb rooms shed from a draining peer (a migration target) and to ride out a burst without
/// being driven to its absolute ceiling. A draining node is skipped entirely (it takes no new rooms).
/// </para>
/// <para>
/// Atomicity: the check-then-claim is guarded by a per-instance lock, which is sufficient only when the
/// <see cref="IRoomDirectory"/> is a single-process one (e.g. <see cref="InMemoryRoomDirectory"/>) — a
/// per-process lock cannot serialize placement across nodes. For a real fleet the capacity-check and
/// claim must be one atomic store operation; that is the job of the distributed placement
/// (<c>RedisRoomPlacement</c>, a single Lua script), NOT this class over a remote directory.
/// </para>
/// </remarks>
public sealed class CapacityAwareRoomPlacement : IRoomPlacement
{
    private readonly IRoomDirectory _directory;
    private readonly IReadOnlyList<NodeId> _nodes;
    private readonly int _allocatableCeiling;
    private readonly object _gate = new();

    public CapacityAwareRoomPlacement(
        IRoomDirectory directory,
        IReadOnlyList<NodeId> nodes,
        int maxRoomsPerNode,
        int reservedHeadroom = 0)
    {
        if (nodes is null || nodes.Count == 0)
        {
            throw new ArgumentException("At least one node is required.", nameof(nodes));
        }

        if (maxRoomsPerNode < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRoomsPerNode), maxRoomsPerNode, "Capacity must be >= 1.");
        }

        if (reservedHeadroom < 0 || reservedHeadroom >= maxRoomsPerNode)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reservedHeadroom), reservedHeadroom,
                "Reserved headroom must be >= 0 and leave at least one allocatable slot (< maxRoomsPerNode).");
        }

        _directory = directory;
        _nodes = nodes;
        _allocatableCeiling = maxRoomsPerNode - reservedHeadroom;
    }

    public RoomPlacementResult Place(RoomKey room)
    {
        lock (_gate)
        {
            // Idempotent for a LIVE, NON-DRAINING owner: a room already owned by such a node keeps that
            // owner. If the owner is no longer in the fleet (scaled down / replaced) OR is draining, the
            // claim is STALE and must move — but only once a live target is secured (see below), so a room
            // is never released into the void (stranded) when there is nowhere to re-home it.
            var hasStaleOwner = false;
            var staleOwner = default(NodeId);
            if (_directory.TryGetOwner(room, out var existing))
            {
                if (_nodes.Contains(existing) && !_directory.IsNodeDraining(existing))
                {
                    return RoomPlacementResult.OnNode(existing);
                }

                hasStaleOwner = true;
                staleOwner = existing;
            }

            foreach (var node in _nodes)
            {
                // A draining node accepts no new rooms — skip it so its slots are not re-filled while it
                // sheds (the directory's TryClaim also fences this, defense in depth against a race).
                if (_directory.IsNodeDraining(node))
                {
                    continue;
                }

                if (_directory.OwnedCount(node) >= _allocatableCeiling)
                {
                    continue; // node is at its allocatable ceiling (cap minus reserved headroom) — try the next
                }

                // We found a live target. Release the stale claim (if any) and claim for the new node under
                // the same lock, so the move is atomic: there is no observable window where the room is
                // owner-less. Releasing only HERE means a room with no re-home target keeps its old owner.
                if (hasStaleOwner)
                {
                    _directory.Release(room, staleOwner);
                    hasStaleOwner = false;
                }

                // TryClaim is the authority; if it loses a race (another placer/node took the room),
                // converge on whoever won rather than reporting a node we did not actually secure.
                if (_directory.TryClaim(room, node))
                {
                    return RoomPlacementResult.OnNode(node);
                }

                if (_directory.TryGetOwner(room, out var winner))
                {
                    return RoomPlacementResult.OnNode(winner);
                }
            }

            return RoomPlacementResult.ClusterAtCapacity;
        }
    }
}
