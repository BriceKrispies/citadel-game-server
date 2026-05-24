using Xunit;

namespace GameServer.LoadHarness;

public sealed class RoomAssignmentStrategyTests
{
    [Fact]
    public void RoomAssignment_DistributesClientsAcrossRooms()
    {
        var strategy = new RoomAssignmentStrategy(roomCount: 10);

        var counts = new Dictionary<string, int>();
        for (var clientIndex = 0; clientIndex < 100; clientIndex++)
        {
            var room = strategy.AssignRoom(clientIndex);
            counts[room] = counts.GetValueOrDefault(room) + 1;
        }

        Assert.Equal(10, counts.Count);              // all rooms used
        Assert.All(counts.Values, c => Assert.Equal(10, c)); // evenly distributed
        Assert.Equal(10, strategy.AllRooms().Count);
    }
}
