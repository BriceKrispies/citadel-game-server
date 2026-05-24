using GameServer.Protocol;
using Xunit;

namespace GameServer.Tenancy;

public sealed class InMemoryTenantResolverTests
{
    [Fact]
    public void Resolves_KnownTenant()
    {
        var resolver = new InMemoryTenantResolver(new[] { new TenantContext(new TenantId("tenant-a"), "Tenant A") });

        Assert.True(resolver.TryResolve(new TenantId("tenant-a"), out var context));
        Assert.Equal("Tenant A", context.DisplayName);
    }

    [Fact]
    public void Rejects_UnknownTenant()
    {
        var resolver = new InMemoryTenantResolver(Array.Empty<TenantContext>());

        Assert.False(resolver.TryResolve(new TenantId("ghost"), out _));
    }

    [Fact]
    public void Add_RegistersTenant_Fluently()
    {
        var resolver = new InMemoryTenantResolver(Array.Empty<TenantContext>())
            .Add(new TenantContext(new TenantId("tenant-b"), "Tenant B"));

        Assert.True(resolver.TryResolve(new TenantId("tenant-b"), out _));
    }
}
