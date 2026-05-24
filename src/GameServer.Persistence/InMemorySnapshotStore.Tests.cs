using Xunit;

namespace GameServer.Persistence;

public sealed class InMemorySnapshotStoreTests
{
    [Fact]
    public void Save_Then_TryGetLatest_ReturnsLastWrite()
    {
        var store = new InMemorySnapshotStore<string, int>();
        store.Save("room", 1);
        store.Save("room", 7);

        Assert.True(store.TryGetLatest("room", out var latest));
        Assert.Equal(7, latest);
    }

    [Fact]
    public void TryGetLatest_MissesUnknownKey()
    {
        var store = new InMemorySnapshotStore<string, int>();

        Assert.False(store.TryGetLatest("nope", out _));
    }
}
