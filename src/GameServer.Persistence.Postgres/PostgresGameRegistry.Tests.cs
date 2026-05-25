using GameServer.ControlPlane;
using GameServer.Replication;
using Xunit;

namespace GameServer.Persistence.Postgres;

/// <summary>
/// Hermetic checks of the durable game registry's seeding, caching, and tenant routing — NO database. The
/// DB write-through is exercised end-to-end by the Testcontainers integration scenario; here we prove the
/// in-process behavior: a seeded game (and its versions) is readable, a created game routes its durable
/// write to the OWNING tenant's context, and reads serve from the warm cache.
/// </summary>
public sealed class PostgresGameRegistryTests
{
    private sealed class RecordingFactory : ITenantDbContextFactory
    {
        public List<string> Opened { get; } = new();

        public TenantDbContext CreateForTenant(string tenantId)
        {
            Opened.Add(tenantId);
            throw new InvalidOperationException("hermetic: no database available");
        }

        public void Migrate(string tenantId) => throw new NotSupportedException();
    }

    private static PostgresGameRegistry Seeded(RecordingFactory factory) =>
        new(factory, new[]
        {
            new PostgresGameRegistry.Seed(
                "tenant-a",
                new[] { new GameDetail("durable-game", "Durable Game", "", 1, ReplicationPolicy.Default with { SnapshotMode = SnapshotMode.Delta }) },
                new[] { new GameVersionDto("durable-game", 1, "seeded"), new GameVersionDto("durable-game", 2, "v2") }),
        });

    [Fact]
    public void SeededGame_IsReadableFromCache_WithoutTouchingTheDatabase()
    {
        var factory = new RecordingFactory();
        var registry = Seeded(factory);

        Assert.True(registry.TryGet("durable-game", out var game));
        Assert.Equal("Durable Game", game.Name);
        Assert.Equal(SnapshotMode.Delta, registry.GetPolicy("durable-game").SnapshotMode);
        Assert.Contains(registry.ListVersions("durable-game"), v => v.SchemaVersion == 2);
        // Reads never open a tenant database.
        Assert.Empty(factory.Opened);
    }

    [Fact]
    public void CreateGame_RoutesDurableWriteToOwningTenant()
    {
        var factory = new RecordingFactory();
        var registry = Seeded(factory);

        var ex = Record.Exception(() =>
            registry.TryCreateGame("tenant-b", new GameDetail("new-game", "New", "", 1)));

        // The durable write is attempted against the owning tenant's database (tenant-b), not any other.
        Assert.NotNull(ex);
        Assert.Equal(new[] { "tenant-b" }, factory.Opened);
    }

    [Fact]
    public void CreateVersion_ForUnknownGame_IsRejected_WithoutOpeningADatabase()
    {
        var factory = new RecordingFactory();
        var registry = Seeded(factory);

        Assert.False(registry.TryCreateVersion(new GameVersionDto("no-such-game", 9, "")));
        Assert.Empty(factory.Opened);
    }
}
