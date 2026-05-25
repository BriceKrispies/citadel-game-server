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

    [Fact]
    public void CreateGame_MakesItResolvable_WithoutCodeChange()
    {
        var catalog = new InMemoryGameCatalog();

        Assert.True(catalog.TryCreateGame("tenant-a", new GameDetail("new-game", "New Game", "added at runtime", 1,
            ReplicationPolicy.Default with { SnapshotMode = SnapshotMode.Delta })));

        Assert.True(catalog.TryGet("new-game", out var game));
        Assert.Equal("New Game", game.Name);
        // The realtime hot path immediately sees the new game's policy.
        Assert.Equal(SnapshotMode.Delta, catalog.GetPolicy("new-game").SnapshotMode);
        Assert.Contains(catalog.List(), g => g.GameId == "new-game");
    }

    [Fact]
    public void TryGetOwningTenant_RecordsTheCreatorTenant_ForAuthorization()
    {
        var catalog = new InMemoryGameCatalog();

        Assert.True(catalog.TryCreateGame("tenant-a", new GameDetail("owned", "Owned", "", 1)));

        // The mutation gate authorizes against THIS owner, never the caller's own tenant.
        Assert.True(catalog.TryGetOwningTenant("owned", out var owner));
        Assert.Equal("tenant-a", owner);

        // Unknown game has no owner.
        Assert.False(catalog.TryGetOwningTenant("no-such-game", out _));

        // A seeded demo game is owned by no real tenant, so no game-admin's tenant matches it (only a
        // platform-admin, who can act for any tenant, may mutate it).
        Assert.True(catalog.TryGetOwningTenant("demo-game", out var seededOwner));
        Assert.NotEqual("tenant-a", seededOwner);
        Assert.NotEqual("tenant-b", seededOwner);
    }

    [Fact]
    public void DeleteGame_ClearsOwnership()
    {
        var catalog = new InMemoryGameCatalog();

        Assert.True(catalog.TryCreateGame("tenant-a", new GameDetail("owned", "Owned", "", 1)));
        Assert.True(catalog.TryDeleteGame("owned"));
        Assert.False(catalog.TryGetOwningTenant("owned", out _));
    }

    [Fact]
    public void CreateGame_DuplicateId_IsRejected()
    {
        var catalog = new InMemoryGameCatalog();

        Assert.False(catalog.TryCreateGame("tenant-a", new GameDetail("demo-game", "dup", "", 1)));
    }

    [Fact]
    public void DeleteGame_RemovesIt()
    {
        var catalog = new InMemoryGameCatalog();

        Assert.True(catalog.TryDeleteGame("demo-game"));
        Assert.False(catalog.TryGet("demo-game", out _));
        Assert.False(catalog.TryDeleteGame("demo-game")); // already gone
    }

    [Fact]
    public void CreateVersion_AddsToGame_RejectsUnknownGameAndDuplicateVersion()
    {
        var catalog = new InMemoryGameCatalog();

        Assert.True(catalog.TryCreateVersion(new GameVersionDto("demo-game", 2, "v2 notes")));
        Assert.Contains(catalog.ListVersions("demo-game"), v => v.SchemaVersion == 2);

        // Duplicate version for the same game is rejected.
        Assert.False(catalog.TryCreateVersion(new GameVersionDto("demo-game", 2, "dup")));
        // Version for an unknown game is rejected.
        Assert.False(catalog.TryCreateVersion(new GameVersionDto("no-such-game", 1, "")));
    }
}
