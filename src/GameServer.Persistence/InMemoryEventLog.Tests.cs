using Xunit;

namespace GameServer.Persistence;

public sealed class InMemoryEventLogTests
{
    [Fact]
    public void Append_PreservesOrder_PerKey()
    {
        var log = new InMemoryEventLog<string, string>();
        log.Append("room", "a");
        log.Append("room", "b");

        Assert.Equal(new[] { "a", "b" }, log.Read("room"));
    }

    [Fact]
    public void Read_UnknownKey_IsEmpty()
    {
        var log = new InMemoryEventLog<string, string>();

        Assert.Empty(log.Read("nope"));
    }

    [Fact]
    public void Streams_AreIsolated_PerKey()
    {
        var log = new InMemoryEventLog<string, string>();
        log.Append("room-1", "x");
        log.Append("room-2", "y");

        Assert.Equal(new[] { "x" }, log.Read("room-1"));
        Assert.Equal(new[] { "y" }, log.Read("room-2"));
    }
}
