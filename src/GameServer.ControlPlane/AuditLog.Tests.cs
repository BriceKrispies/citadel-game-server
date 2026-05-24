using Xunit;

namespace GameServer.ControlPlane;

public sealed class InMemoryAuditLogTests
{
    private static AuditRecord Record(string action) =>
        new("operator-a", action, "tenant-a/room-1", DateTimeOffset.UnixEpoch, "allowed");

    [Fact]
    public void Read_ReturnsRecords_InAppendOrder()
    {
        var log = new InMemoryAuditLog();

        log.Record(Record("create-room"));
        log.Record(Record("mint-join-token"));

        var records = log.Read();
        Assert.Equal(2, records.Count);
        Assert.Equal("create-room", records[0].Action);
        Assert.Equal("mint-join-token", records[1].Action);
    }

    [Fact]
    public void Read_OnEmptyLog_IsEmpty()
    {
        Assert.Empty(new InMemoryAuditLog().Read());
    }
}
