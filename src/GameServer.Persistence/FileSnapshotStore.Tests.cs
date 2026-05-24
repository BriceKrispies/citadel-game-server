using Xunit;

namespace GameServer.Persistence;

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
        using var dir = new TempDir();
        ISnapshotStore<string, string> store = new FileSnapshotStore<string, string>(dir.Path);
        Assert.NotNull(store);
    }

    [Fact]
    public void TryGetLatest_OnEmptyStore_IsFalse()
    {
        using var dir = new TempDir();
        var store = new FileSnapshotStore<string, string>(dir.Path);
        Assert.False(store.TryGetLatest("missing", out _));
    }

    [Fact]
    public void Save_ThenGet_RoundTrips()
    {
        using var dir = new TempDir();
        var store = new FileSnapshotStore<string, string>(dir.Path);
        store.Save("k", "value-1");

        Assert.True(store.TryGetLatest("k", out var value));
        Assert.Equal("value-1", value);
    }

    [Fact]
    public void Save_IsLastWriteWins_PerKey()
    {
        using var dir = new TempDir();
        var store = new FileSnapshotStore<string, string>(dir.Path);
        store.Save("k", "first");
        store.Save("k", "second");

        Assert.True(store.TryGetLatest("k", out var value));
        Assert.Equal("second", value);
    }

    [Fact]
    public void WrittenSnapshot_SurvivesIntoAFreshStoreInstance()
    {
        using var dir = new TempDir();
        new FileSnapshotStore<string, int>(dir.Path).Save("k", 42);

        // A new instance pointed at the same directory = a process restart.
        var afterRestart = new FileSnapshotStore<string, int>(dir.Path);
        Assert.True(afterRestart.TryGetLatest("k", out var value));
        Assert.Equal(42, value);
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "citadel-test-" + Guid.NewGuid().ToString("n"));

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup of a temp directory; never fail a test on teardown.
            }
        }
    }
}
