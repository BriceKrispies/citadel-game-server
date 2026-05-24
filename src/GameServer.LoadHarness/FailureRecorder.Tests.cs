using Xunit;

namespace GameServer.LoadHarness;

public sealed class FailureRecorderTests
{
    [Fact]
    public void FailureRecorder_RecordsUnexpectedClose()
    {
        var failures = new FailureRecorder();

        failures.RecordUnexpectedClose(clientId: 7, "server closed mid-stream");

        Assert.Equal(1, failures.CountOf(FailureCategory.UnexpectedClose));
        var entry = Assert.Single(failures.Entries);
        Assert.Equal(7, entry.ClientId);
        Assert.Equal(FailureCategory.UnexpectedClose, entry.Category);
        Assert.Contains("mid-stream", entry.Detail);
    }
}
