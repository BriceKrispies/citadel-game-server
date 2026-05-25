using Xunit;

namespace GameServer.Persistence.Postgres;

public sealed class DesignTimeTenantDbContextFactoryTests
{
    [Fact]
    public void CreatesAContext_ForTheEfTools_WithoutOpeningAConnection()
    {
        // The EF tools call this to scaffold migrations; it must produce a usable context shape with no
        // live database. Constructing the context does not connect, so this stays hermetic.
        using var db = new DesignTimeTenantDbContextFactory().CreateDbContext(Array.Empty<string>());

        Assert.NotNull(db);
        Assert.NotEmpty(db.Model.GetEntityTypes());
    }
}
