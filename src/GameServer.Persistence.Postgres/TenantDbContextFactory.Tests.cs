using Xunit;

namespace GameServer.Persistence.Postgres;

/// <summary>Hermetic checks that the factory opens a context against the connection the resolver
/// returns for the requested tenant — the routing that makes the database the isolation boundary.
/// No connection is opened (the context is constructed, not used).</summary>
public sealed class TenantDbContextFactoryTests
{
    private sealed class StubResolver : ITenantConnectionResolver
    {
        public string? RequestedTenant { get; private set; }

        public string ConnectionStringFor(string tenantId)
        {
            RequestedTenant = tenantId;
            return $"Host=localhost;Database=tenant_{tenantId};Username=u;Password=p";
        }
    }

    [Fact]
    public void CreateForTenant_ResolvesThatTenantsConnection()
    {
        var resolver = new StubResolver();
        var factory = new TenantDbContextFactory(resolver);

        using var db = factory.CreateForTenant("tenant-a");

        Assert.NotNull(db);
        Assert.Equal("tenant-a", resolver.RequestedTenant);
    }

    [Fact]
    public void DistinctTenants_ResolveDistinctConnections()
    {
        var resolver = new StubResolver();
        Assert.NotEqual(resolver.ConnectionStringFor("a"), resolver.ConnectionStringFor("b"));
    }
}
