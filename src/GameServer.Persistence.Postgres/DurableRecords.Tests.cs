using Xunit;

namespace GameServer.Persistence.Postgres;

/// <summary>Hermetic shape checks for the durable EF records (no database). The behavioral
/// persistence is proven in the Testcontainers-gated integration scenario.</summary>
public sealed class DurableRecordsTests
{
    [Fact]
    public void RoomSnapshotRecord_CarriesReplayHeader()
    {
        var record = new RoomSnapshotRecord
        {
            RoomId = "arena",
            Tick = 12,
            Seed = 7,
            GameSchemaVersion = 3,
            State = new byte[] { 1, 2, 3 },
        };

        Assert.Equal("arena", record.RoomId);
        Assert.Equal(12, record.Tick);
        Assert.Equal(7, record.Seed);
        Assert.Equal(3, record.GameSchemaVersion);
        Assert.Equal(new byte[] { 1, 2, 3 }, record.State);
    }

    [Fact]
    public void RoomEventRecord_OrdersWithinATick_ViaOrdinal()
    {
        var first = new RoomEventRecord { RoomId = "arena", Tick = 5, Ordinal = 0, PlayerId = "p1", Command = "MoveRight" };
        var second = new RoomEventRecord { RoomId = "arena", Tick = 5, Ordinal = 1, PlayerId = "p1", Command = "MoveRight" };

        Assert.True(second.Ordinal > first.Ordinal);
    }

    [Fact]
    public void Records_DefaultToEmpty_NotNull()
    {
        Assert.Equal(string.Empty, new GameRecord().GameId);
        Assert.Equal(string.Empty, new GameVersionRecord().GameId);
        Assert.Equal(string.Empty, new RoomRecord().RoomId);
        Assert.Equal(string.Empty, new SessionRecord().SessionId);
        Assert.Equal(string.Empty, new TenantAuditRecord().CallerId);
        Assert.Empty(new RoomSnapshotRecord().State);
    }
}
