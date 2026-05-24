using GameServer.Replication;
using Xunit;

namespace GameServer.ControlPlane;

public sealed class InMemoryGameCatalogTests
{
    [Fact]
    public void GetPolicy_ReflectsPerGameConfig_AndDefaultsForUnknown()
    {
        var catalog = new InMemoryGameCatalog();

        Assert.Equal(SnapshotMode.Full, catalog.GetPolicy("demo-game").SnapshotMode);
        Assert.Equal(SnapshotMode.Delta, catalog.GetPolicy("demo-delta").SnapshotMode);
        Assert.True(catalog.GetPolicy("demo-budget").PerClientBudgetBytes > 0);

        // Unknown game falls back to the conservative platform default.
        Assert.Equal(ReplicationPolicy.Default, catalog.GetPolicy("no-such-game"));
    }

    [Fact]
    public void Lists_SeededGame()
    {
        var catalog = new InMemoryGameCatalog();

        Assert.Contains(catalog.List(), g => g.GameId == "demo-game");
    }

    [Fact]
    public void TryGet_FindsKnownGame_MissesUnknown()
    {
        var catalog = new InMemoryGameCatalog();

        Assert.True(catalog.TryGet("demo-game", out var game));
        Assert.Equal("demo-game", game.GameId);
        Assert.False(catalog.TryGet("no-such-game", out _));
    }
}
