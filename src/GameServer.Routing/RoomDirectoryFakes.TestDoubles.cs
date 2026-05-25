namespace GameServer.Routing;

/// <summary>
/// A working in-memory <see cref="IRoomDirectory"/> for tests that need real ownership state to
/// exercise a <em>different</em> seam (affinity routing, placement). It is honest single-process
/// behavior — claim is fenced, release frees — but it is NOT a production directory: an in-process
/// dictionary gives no cross-node ownership, which is the whole point of the unimplemented
/// <see cref="ClusterRoomDirectory"/>. Test-only (excluded from production assemblies).
/// </summary>
internal sealed class FakeRoomDirectory : IRoomDirectory
{
    private readonly Dictionary<RoomKey, NodeId> _owners = new();
    private readonly HashSet<NodeId> _draining = new();

    public bool TryClaim(RoomKey room, NodeId owner)
    {
        if (_owners.TryGetValue(room, out var existing))
        {
            return existing == owner; // already owned: idempotent for the holder, fenced for others
        }

        if (_draining.Contains(owner))
        {
            return false; // a draining node accepts no new rooms
        }

        _owners[room] = owner;
        return true;
    }

    // Renew-only: confirms the caller is still the owner; never acquires (mirrors the production contract).
    public bool TryRenew(RoomKey room, NodeId owner) =>
        _owners.TryGetValue(room, out var existing) && existing.Equals(owner);

    public bool TryGetOwner(RoomKey room, out NodeId owner) => _owners.TryGetValue(room, out owner);

    public int OwnedCount(NodeId owner) => _owners.Values.Count(o => o.Equals(owner));

    public void Release(RoomKey room, NodeId owner)
    {
        if (_owners.TryGetValue(room, out var existing) && existing == owner)
        {
            _owners.Remove(room);
        }
    }

    public void SetNodeDraining(NodeId node, bool draining)
    {
        if (draining)
        {
            _draining.Add(node);
        }
        else
        {
            _draining.Remove(node);
        }
    }

    public bool IsNodeDraining(NodeId node) => _draining.Contains(node);

    public IReadOnlyCollection<RoomKey> OwnedRooms(NodeId owner) =>
        _owners.Where(e => e.Value.Equals(owner)).Select(e => e.Key).ToArray();
}
