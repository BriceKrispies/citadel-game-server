namespace GameServer.Routing;

/// <summary>
/// Identifies one node (process / ECS task) in the realtime cluster. <see cref="Value"/> is the node's
/// externally-reachable base address (e.g. "https://node-3.citadel.internal:5000"), so a routing
/// redirect can carry it directly — no separate node→address registry is needed.
/// </summary>
public readonly record struct NodeId(string Value);

/// <summary>
/// The room directory: the source of truth for which node owns a given <see cref="RoomKey"/>. Exactly
/// one node may own a room at a time — ownership is claimed (fenced), looked up, counted per node, and
/// released. This is what lets a fleet avoid split brain (two nodes ticking the same room), as
/// demonstrated by <c>MultiNodeOwnershipScenario</c>.
/// </summary>
/// <remarks>
/// Backends: <see cref="InMemoryRoomDirectory"/> is single-process authoritative (one node / shared-instance
/// tests); a distributed backend (e.g. <c>RedisRoomDirectory</c> in <c>GameServer.Cluster.Redis</c>) provides
/// real cross-node ownership behind this same contract — the InMemory↔distributed split mirrors
/// InMemory↔durable snapshot stores. All implementations must claim atomically (fence concurrent claimers),
/// release only for the current owner, and count by owner.
///
/// Ownership is a renewable LEASE: a distributed backend may expire a claim after a bounded window, so an
/// owner must renew while it holds a room (the host runs a renewal worker) and release on teardown. The
/// in-memory backend's lease is effectively infinite — a valid lease duration — so a renewing caller
/// behaves identically on both backends.
/// </remarks>
public interface IRoomDirectory
{
    /// <summary>
    /// Claims ownership of <paramref name="room"/> for <paramref name="owner"/>. Returns false if a
    /// different node already owns it (fencing); idempotently true if <paramref name="owner"/> already
    /// holds it. Must be atomic against concurrent claimers — exactly one wins.
    /// </summary>
    bool TryClaim(RoomKey room, NodeId owner);

    /// <summary>Returns the current owner of <paramref name="room"/>, if any node owns it.</summary>
    bool TryGetOwner(RoomKey room, out NodeId owner);

    /// <summary>The number of rooms <paramref name="owner"/> currently owns. The basis for capacity-aware placement.</summary>
    int OwnedCount(NodeId owner);

    /// <summary>Releases <paramref name="room"/> if <paramref name="owner"/> currently holds it; otherwise a no-op.</summary>
    void Release(RoomKey room, NodeId owner);

    /// <summary>
    /// Marks <paramref name="node"/> as draining (<paramref name="draining"/> = true) or active again.
    /// A draining node is removed from the placement pool — it accepts no new allocations — so the drain
    /// decision is fleet-visible (every node's placement reads it from the shared directory), not local
    /// to one process. Idempotent.
    /// </summary>
    void SetNodeDraining(NodeId node, bool draining);

    /// <summary>True if <paramref name="node"/> is currently draining (refusing new allocations).</summary>
    bool IsNodeDraining(NodeId node);

    /// <summary>
    /// The rooms <paramref name="owner"/> currently owns. A draining node enumerates these to shed each
    /// one (release it so it re-places on a live node). Ordering is unspecified.
    /// </summary>
    IReadOnlyCollection<RoomKey> OwnedRooms(NodeId owner);
}
