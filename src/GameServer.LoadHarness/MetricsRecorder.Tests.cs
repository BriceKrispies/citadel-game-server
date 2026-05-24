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
}
