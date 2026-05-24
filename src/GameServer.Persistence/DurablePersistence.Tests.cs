using Xunit;

namespace GameServer.Persistence;

/// <summary>
/// Contract guards for the <see cref="FileSnapshotStore{TKey,TSnapshot}"/> durable seam:
/// construction validates the directory, and save/read report they are not implemented yet.
/// A durable store must also be substitutable for the in-memory one (LSP), so it implements
/// <see cref="ISnapshotStore{TKey,TSnapshot}"/> via <see cref="IDurableSnapshotStore{TKey,TSnapshot}"/>.
/// </summary>
public sealed class DurablePersistenceTests
{
    [Fact]
    public void Construction_WithBlankDirectory_Throws()
    {
        Assert.Throws<ArgumentException>(() => new FileSnapshotStore<string, string>("  "));
    }

    [Fact]
    public void DurableStore_IsSubstitutableForTheSnapshotStoreContract()
    {
        ISnapshotStore<string, string> store = new FileSnapshotStore<string, string>("snapshots");
        Assert.NotNull(store);
    }

    [Fact]
    public void Save_IsNotImplementedYet()
    {
        var store = new FileSnapshotStore<string, string>("snapshots");
        Assert.Throws<NotImplementedException>(() => store.Save("k", "v"));
    }

    [Fact]
    public void TryGetLatest_IsNotImplementedYet()
    {
        var store = new FileSnapshotStore<string, string>("snapshots");
        Assert.Throws<NotImplementedException>(() => store.TryGetLatest("k", out _));
    }
}
