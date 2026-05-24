namespace GameServer.LoadHarness;

/// <summary>
/// Deterministically assigns virtual clients to rooms. Round-robin over a fixed set
/// of room ids spreads load evenly; a scenario achieves fan-out by setting a small
/// room count with a high client count.
///
/// The room ids are the REAL ids the control plane assigned when the rooms were
/// provisioned (see <see cref="JoinTokenProvider.ProvisionRoomsAsync"/>). This matters:
/// a client's join token is scoped to a specific room, and the realtime server rejects
/// a <c>ClientJoinRoom</c> whose room id does not match the token's claim. Synthesizing
/// names like "room-1" here would only work if they happened to equal the server's
/// assigned ids — so the runner threads the provisioned ids through instead.
/// </summary>
public sealed class RoomAssignmentStrategy
{
    private readonly IReadOnlyList<string> _roomIds;

    /// <summary>Assigns across an explicit set of (control-plane assigned) room ids.</summary>
    public RoomAssignmentStrategy(IReadOnlyList<string> roomIds)
    {
        if (roomIds is null || roomIds.Count < 1)
        {
            throw new ArgumentException("At least one room id is required.", nameof(roomIds));
        }

        _roomIds = roomIds;
    }

    /// <summary>
    /// Assigns across synthesized ids "room-1".."room-{roomCount}". Used only where no
    /// control-plane provisioning happens (e.g. static-token custom setups and tests).
    /// </summary>
    public RoomAssignmentStrategy(int roomCount)
        : this(Synthesize(roomCount))
    {
    }

    private static IReadOnlyList<string> Synthesize(int roomCount)
    {
        if (roomCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(roomCount), roomCount, "roomCount must be >= 1.");
        }

        return Enumerable.Range(1, roomCount).Select(n => $"room-{n}").ToArray();
    }

    public int RoomCount => _roomIds.Count;

    /// <summary>The room id for a given zero-based client index.</summary>
    public string AssignRoom(int clientIndex) => _roomIds[clientIndex % _roomIds.Count];

    public IReadOnlyList<string> AllRooms() => _roomIds;
}
