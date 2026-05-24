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

    public bool TryClaim(RoomKey room, NodeId owner) => _owners.GetOrAdd(room, owner).Equals(owner);

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
}
