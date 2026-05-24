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

    [Fact]
    public void RoomAssignment_FromProvisionedRoomIds_UsesRealIdsNotSynthesizedNames()
    {
        // The control plane assigns ids; the strategy must hand back exactly those, so a
        // client's ClientJoinRoom matches its token's room claim (RealtimeServer rejects a
        // mismatch). 4 rooms, 200 clients → 50 each, round-robin.
        var provisioned = new[] { "room-7", "room-8", "room-9", "room-10" };
        var strategy = new RoomAssignmentStrategy(provisioned);

        var counts = new Dictionary<string, int>();
        for (var clientIndex = 0; clientIndex < 200; clientIndex++)
        {
            counts[strategy.AssignRoom(clientIndex)] = counts.GetValueOrDefault(strategy.AssignRoom(clientIndex)) + 1;
        }

        Assert.Equal(provisioned, strategy.AllRooms());
        Assert.Equal(4, counts.Count);
        Assert.All(counts.Values, c => Assert.Equal(50, c));
        Assert.Equal("room-7", strategy.AssignRoom(0));
        Assert.Equal("room-8", strategy.AssignRoom(1));
        Assert.Equal("room-7", strategy.AssignRoom(4));
    }

    [Fact]
    public void RoomAssignment_RejectsEmptyRoomList()
    {
        Assert.Throws<ArgumentException>(() => new RoomAssignmentStrategy(Array.Empty<string>()));
    }
}
