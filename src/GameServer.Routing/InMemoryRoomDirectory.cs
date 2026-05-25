using System.Collections.Concurrent;

namespace GameServer.Routing;

/// <summary>
/// Single-process authoritative <see cref="IRoomDirectory"/>: a thread-safe in-memory owner map. It is
/// the directory for a single node, and the directory that shared-instance tests use to model "shared
/// infrastructure". It is NOT cross-node by itself — two processes each get their own map; real fleet
/// ownership comes from a distributed backend (Redis/DynamoDB) implementing this same contract, exactly
/// as a durable snapshot store backs the same <c>ISnapshotStore</c> as the in-memory one.
///
/// Concurrency: claim is a single atomic <see cref="ConcurrentDictionary{TKey,TValue}.GetOrAdd"/> (first
/// claimer wins, same-owner is idempotent); release is an atomic compare-and-remove gated on the current
/// owner, so a stale owner cannot evict a newer one.
/// </summary>
public sealed class InMemoryRoomDirectory : IRoomDirectory
{
    private readonly ConcurrentDictionary<RoomKey, NodeId> _owners = new();
    private readonly ConcurrentDictionary<NodeId, byte> _draining = new();

    // A claim is refused on a draining node so its slots are not re-filled while it sheds. The check is
    // part of the same GetOrAdd path so a node entering drain cannot win a concurrent first-claim.
    public bool TryClaim(RoomKey room, NodeId owner)
    {
        if (_draining.ContainsKey(owner) && (!_owners.TryGetValue(room, out var existing) || !existing.Equals(owner)))
        {
            // Draining node: refuse to take a NEW room. Renewing a room it already owns stays allowed so a
            // mid-drain lease does not lapse before the room is shed (no stranded/double-owned room).
            return false;
        }

        return _owners.GetOrAdd(room, owner).Equals(owner);
    }

    public bool TryGetOwner(RoomKey room, out NodeId owner) => _owners.TryGetValue(room, out owner);

    public int OwnedCount(NodeId owner)
    {
        var count = 0;
        foreach (var entry in _owners)
        {
            if (entry.Value.Equals(owner))
            {
                count++;
            }
        }

        return count;
    }

    public void Release(RoomKey room, NodeId owner) =>
        // Removes only if the room is still owned by exactly this owner (atomic compare-and-remove).
        ((ICollection<KeyValuePair<RoomKey, NodeId>>)_owners).Remove(new KeyValuePair<RoomKey, NodeId>(room, owner));

    public void SetNodeDraining(NodeId node, bool draining)
    {
        if (draining)
        {
            _draining[node] = 0;
        }
        else
        {
            _draining.TryRemove(node, out _);
        }
    }

    public bool IsNodeDraining(NodeId node) => _draining.ContainsKey(node);

    public IReadOnlyCollection<RoomKey> OwnedRooms(NodeId owner)
    {
        var rooms = new List<RoomKey>();
        foreach (var entry in _owners)
        {
            if (entry.Value.Equals(owner))
            {
                rooms.Add(entry.Key);
            }
        }

        return rooms;
    }
}
