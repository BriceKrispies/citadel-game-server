using Xunit;

namespace GameServer.LoadHarness;

public sealed class MetricsRecorderTests
{
    [Fact]
    public void MetricsRecorder_RecordsConnectHandshakeJoinLatency()
    {
        var metrics = new MetricsRecorder();

        metrics.RecordConnectLatency(TimeSpan.FromMilliseconds(5));
        metrics.RecordHandshakeLatency(TimeSpan.FromMilliseconds(10));
        metrics.RecordJoinLatency(TimeSpan.FromMilliseconds(20));
        metrics.RecordInputToSnapshotLatency(TimeSpan.FromMilliseconds(30));

        var snapshot = metrics.Snapshot();

        Assert.Equal(1, snapshot.ConnectLatency.Count);
        Assert.Equal(5, snapshot.ConnectLatency.MaxMs);
        Assert.Equal(1, snapshot.HandshakeLatency.Count);
        Assert.Equal(10, snapshot.HandshakeLatency.MaxMs);
        Assert.Equal(1, snapshot.JoinLatency.Count);
        Assert.Equal(20, snapshot.JoinLatency.MaxMs);
        Assert.Equal(30, snapshot.InputToSnapshotLatency.P95Ms);
    }

    [Fact]
    public void MetricsRecorder_TalliesSnapshotsPerRoom()
    {
        var metrics = new MetricsRecorder();

        // Two clients receiving in room-1, one in room-2.
        metrics.RecordRoomClientReceiving("room-1");
        metrics.RecordRoomSnapshot("room-1", serverTick: 5, lagTicks: 1);
        metrics.RecordRoomClientReceiving("room-1");
        metrics.RecordRoomSnapshot("room-1", serverTick: 9, lagTicks: 3);
        metrics.RecordRoomClientReceiving("room-2");
        metrics.RecordRoomSnapshot("room-2", serverTick: 4, lagTicks: 0);

        var perRoom = metrics.Snapshot().PerRoom;

        Assert.Equal(2, perRoom.Count);
        var room1 = Assert.Single(perRoom, m => m.Room == "room-1");
        Assert.Equal(2, room1.ClientsReceiving);
        Assert.Equal(2, room1.SnapshotsReceived);
        Assert.Equal(9ul, room1.MaxServerTick);
        Assert.Equal(3, room1.MaxSnapshotLagTicks);

        var room2 = Assert.Single(perRoom, m => m.Room == "room-2");
        Assert.Equal(1, room2.ClientsReceiving);
        Assert.Equal(4ul, room2.MaxServerTick);
    }
}
