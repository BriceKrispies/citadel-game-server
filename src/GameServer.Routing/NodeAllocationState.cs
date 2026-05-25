namespace GameServer.Routing;

/// <summary>
/// A node's allocation state in the fleet. A node is <see cref="Active"/> (accepts new room
/// allocations) or <see cref="Draining"/> (refuses new allocations and sheds its rooms so they
/// re-place elsewhere). Draining is how a node leaves the fleet cleanly — for a deploy, scale-in, or
/// health eviction — without stranding a room (lost) or letting two nodes co-own one (split brain).
/// </summary>
public enum NodeAllocationState
{
    /// <summary>The node accepts new room allocations and serves the rooms it owns.</summary>
    Active,

    /// <summary>The node refuses new allocations and sheds existing rooms; it is leaving the fleet.</summary>
    Draining,
}

/// <summary>
/// The lifecycle of a single room's placement across the cluster, beyond the directory's raw
/// claim/release. A room is <see cref="Reserved"/> when a slot has been secured on a node but the room
/// is not yet live, <see cref="Allocated"/> once it is owned and serving, <see cref="Draining"/> while
/// its owning node is shedding it (it is being migrated to another node), and <see cref="Released"/>
/// once ownership is dropped (reaped, or handed off). Modelled explicitly so transitions are legal,
/// observable, and testable rather than implied by scattered claim/release calls.
/// </summary>
public enum RoomAllocationState
{
    /// <summary>A capacity slot is secured on an owner, but the room is not yet live.</summary>
    Reserved,

    /// <summary>The room is owned and actively served by its node.</summary>
    Allocated,

    /// <summary>The owning node is draining; the room is being shed/migrated to another node.</summary>
    Draining,

    /// <summary>Ownership has been dropped (reaped or handed off); the room is no longer placed.</summary>
    Released,
}

/// <summary>
/// The legal transitions of a room's allocation lifecycle. A room lifecycle is a small state machine:
/// it is born <see cref="RoomAllocationState.Reserved"/>, becomes <see cref="RoomAllocationState.Allocated"/>
/// when it goes live, may enter <see cref="RoomAllocationState.Draining"/> when its node sheds it, and
/// ends <see cref="RoomAllocationState.Released"/>. A drained room re-enters the lifecycle as
/// <see cref="RoomAllocationState.Reserved"/> on its new owner. Encoded as an explicit, exhaustive map so
/// an illegal transition (e.g. a released room jumping straight to allocated, or skipping reservation) is
/// rejected loudly instead of silently corrupting ownership accounting.
/// </summary>
public static class RoomAllocationLifecycle
{
    private static readonly IReadOnlyDictionary<RoomAllocationState, IReadOnlySet<RoomAllocationState>> Legal =
        new Dictionary<RoomAllocationState, IReadOnlySet<RoomAllocationState>>
        {
            // A reserved slot goes live, or is released without ever serving (reservation cancelled).
            [RoomAllocationState.Reserved] = Set(RoomAllocationState.Allocated, RoomAllocationState.Released),
            // A live room can begin draining (node leaving) or be released directly (last player left / reap).
            [RoomAllocationState.Allocated] = Set(RoomAllocationState.Draining, RoomAllocationState.Released),
            // A draining room finishes by being released from its old owner (it re-reserves on the new one).
            [RoomAllocationState.Draining] = Set(RoomAllocationState.Released),
            // A released room re-enters only by being reserved again (a fresh placement on some node).
            [RoomAllocationState.Released] = Set(RoomAllocationState.Reserved),
        };

    /// <summary>True if <paramref name="from"/> → <paramref name="to"/> is a legal lifecycle transition.</summary>
    public static bool CanTransition(RoomAllocationState from, RoomAllocationState to) =>
        Legal.TryGetValue(from, out var allowed) && allowed.Contains(to);

    /// <summary>
    /// Returns <paramref name="to"/> if the transition from <paramref name="from"/> is legal; otherwise
    /// throws <see cref="InvalidOperationException"/>. Use at the seam that advances a room's lifecycle so
    /// an illegal move fails fast (and observably) instead of leaving ownership accounting inconsistent.
    /// </summary>
    public static RoomAllocationState Transition(RoomAllocationState from, RoomAllocationState to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException(
                $"Illegal room allocation transition {from} -> {to}.");
        }

        return to;
    }

    private static IReadOnlySet<RoomAllocationState> Set(params RoomAllocationState[] states) =>
        new HashSet<RoomAllocationState>(states);
}
