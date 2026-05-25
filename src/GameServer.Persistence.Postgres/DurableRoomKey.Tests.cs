using Xunit;

namespace GameServer.Persistence.Postgres;

public sealed class DurableRoomKeyTests
{
    [Fact]
    public void Holds_TenantAndRoom()
    {
        var key = new DurableRoomKey("tenant-a", "arena");

        Assert.Equal("tenant-a", key.TenantId);
        Assert.Equal("arena", key.RoomId);
    }

    [Theory]
    [InlineData("", "arena")]
    [InlineData("  ", "arena")]
    [InlineData("tenant-a", "")]
    [InlineData("tenant-a", "   ")]
    public void RejectsMissingParts(string tenantId, string roomId)
    {
        // A blank tenant id would let a write escape the per-tenant database routing; refuse it loudly.
        Assert.Throws<ArgumentException>(() => new DurableRoomKey(tenantId, roomId));
    }

    [Fact]
    public void Equality_IsByValue()
    {
        Assert.Equal(new DurableRoomKey("t", "r"), new DurableRoomKey("t", "r"));
        Assert.NotEqual(new DurableRoomKey("t", "r"), new DurableRoomKey("t", "r2"));
        // Distinct tenants with the same room id are DIFFERENT keys — the isolation-safe property.
        Assert.NotEqual(new DurableRoomKey("a", "r"), new DurableRoomKey("b", "r"));
    }
}
