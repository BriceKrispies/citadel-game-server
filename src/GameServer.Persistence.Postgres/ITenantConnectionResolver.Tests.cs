using Xunit;

namespace GameServer.Persistence.Postgres;

public sealed class DictionaryTenantConnectionResolverTests
{
    [Fact]
    public void ReturnsTheMappedConnectionPerTenant()
    {
        var resolver = new DictionaryTenantConnectionResolver(new Dictionary<string, string>
        {
            ["tenant-a"] = "Host=a;Database=tenant_a",
            ["tenant-b"] = "Host=b;Database=tenant_b",
        });

        Assert.Equal("Host=a;Database=tenant_a", resolver.ConnectionStringFor("tenant-a"));
        Assert.Equal("Host=b;Database=tenant_b", resolver.ConnectionStringFor("tenant-b"));
    }

    [Fact]
    public void UnmappedTenant_Throws_RatherThanFallingBackToASharedDatabase()
    {
        var resolver = new DictionaryTenantConnectionResolver(new Dictionary<string, string>
        {
            ["tenant-a"] = "Host=a;Database=tenant_a",
        });

        // The adversarial case: an unknown tenant must NOT route to a default/shared database.
        Assert.Throws<InvalidOperationException>(() => resolver.ConnectionStringFor("tenant-unknown"));
    }
}
