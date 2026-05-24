namespace GameServer.Routing;

/// <summary>
/// Places a room on the first configured node with spare capacity and records the assignment in the
/// <see cref="IRoomDirectory"/>. Placement is idempotent — an already-owned room returns its existing
/// owner — and capacity-aware: it skips nodes already at <c>maxRoomsPerNode</c> and reports
/// <see cref="RoomPlacementStatus.ClusterAtCapacity"/> when none have room.
/// </summary>
/// <remarks>
/// Atomicity: the check-then-claim is guarded by a per-instance lock, which is sufficient only when the
/// <see cref="IRoomDirectory"/> is a single-process one (e.g. <see cref="InMemoryRoomDirectory"/>) — a
/// per-process lock cannot serialize placement across nodes. For a real fleet the capacity-check and
/// claim must be one atomic store operation; that is the job of the distributed placement
/// (<c>RedisRoomPlacement</c>, a single Lua script), NOT this class over a remote directory.
/// </remarks>
public sealed class CapacityAwareRoomPlacement : IRoomPlacement
{
    private readonly IRoomDirectory _directory;
    private readonly IReadOnlyList<NodeId> _nodes;
    private readonly int _maxRoomsPerNode;
    private readonly object _gate = new();

    public CapacityAwareRoomPlacement(IRoomDirectory directory, IReadOnlyList<NodeId> nodes, int maxRoomsPerNode)
    {
        if (nodes is null || nodes.Count == 0)
        {
            throw new ArgumentException("At least one node is required.", nameof(nodes));
        }

        if (maxRoomsPerNode < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRoomsPerNode), maxRoomsPerNode, "Capacity must be >= 1.");
        }

        _directory = directory;
        _nodes = nodes;
        _maxRoomsPerNode = maxRoomsPerNode;
    }

    public RoomPlacementResult Place(RoomKey room)
    {
        lock (_gate)
        {
            // Idempotent: a room already owned (by any node) keeps that owner.
            if (_directory.TryGetOwner(room, out var existing))
            {
                return RoomPlacementResult.OnNode(existing);
            }

            foreach (var node in _nodes)
            {
                if (_directory.OwnedCount(node) >= _maxRoomsPerNode)
                {
                    continue; // node is full — try the next
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
