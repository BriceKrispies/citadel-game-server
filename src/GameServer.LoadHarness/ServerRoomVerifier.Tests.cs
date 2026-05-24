using Xunit;

namespace GameServer.LoadHarness;

public sealed class ServerRoomVerifierTests
{
    // Mirrors the GET /api/v1/admin/rooms response shape (Program.cs admin-list-rooms).
    private const string AdminRoomsJson = """
    {
      "rooms": [
        { "tenantId": "tenant-a", "roomId": "room-1", "tick": 120, "subscriberCount": 200, "entityCount": 200 },
        { "tenantId": "tenant-a", "roomId": "room-2", "tick": 119, "subscriberCount": 200, "entityCount": 200 },
        { "tenantId": "tenant-b", "roomId": "room-9", "tick": 7,   "subscriberCount": 5,   "entityCount": 5 }
      ],
      "hottest": [ { "room": "tenant-a/room-1", "meanMs": 0.4, "maxMs": 1.1 } ]
    }
    """;

    [Fact]
    public void Parse_KeepsOnlyTheRequestedTenantsRooms()
    {
        var rooms = ServerRoomVerifier.Parse(AdminRoomsJson, "tenant-a");

        Assert.Equal(2, rooms.Count);
        Assert.All(rooms, r => Assert.Equal("tenant-a", r.TenantId));

        var room1 = Assert.Single(rooms, r => r.RoomId == "room-1");
        Assert.Equal(120, room1.Tick);
        Assert.Equal(200, room1.SubscriberCount);
        Assert.Equal(200, room1.EntityCount);
    }

    [Fact]
    public void Parse_MissingRoomsArray_ReturnsEmpty()
    {
        Assert.Empty(ServerRoomVerifier.Parse("{}", "tenant-a"));
    }
}
