namespace GameServer.LoadHarness;

/// <summary>
/// Deterministically assigns virtual clients to rooms. Round-robin over
/// <c>roomCount</c> rooms spreads load evenly; a scenario achieves fan-out by
/// setting a small room count with a high client count.
/// </summary>
public sealed class RoomAssignmentStrategy
{
    private readonly int _roomCount;

    public RoomAssignmentStrategy(int roomCount)
    {
        if (roomCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(roomCount), roomCount, "roomCount must be >= 1.");
        }

        _roomCount = roomCount;
    }

    public static RoomAssignmentStrategy FromConfig(ScenarioConfig config) => new(config.RoomCount);

    /// <summary>The room id for a given zero-based client index.</summary>
    public string AssignRoom(int clientIndex)
    {
        var roomNumber = (clientIndex % _roomCount) + 1;
        return $"room-{roomNumber}";
    }

    public IReadOnlyList<string> AllRooms() =>
        Enumerable.Range(1, _roomCount).Select(n => $"room-{n}").ToArray();
}
