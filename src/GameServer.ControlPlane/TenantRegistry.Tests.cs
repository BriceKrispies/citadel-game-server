using GameServer.Protocol;
using Xunit;

namespace GameServer.ControlPlane;

public sealed class InMemoryTenantRegistryTests
{
    [Fact]
    public void CreatedTenant_IsImmediatelyResolvable_WithoutCodeChange()
    {
        var registry = new InMemoryTenantRegistry();

        Assert.True(registry.TryCreate(new TenantRecordDto("tenant-x", "Tenant X")));

        // The control-plane read.
        Assert.True(registry.TryGet("tenant-x", out var record));
        Assert.Equal("Tenant X", record.DisplayName);

        // The realtime edge resolves the SAME instance: a tenant provisioned via the API is resolvable
        // at the hot path with no redeploy.
        Assert.True(registry.TryResolve(new TenantId("tenant-x"), out var context));
        Assert.Equal("Tenant X", context.DisplayName);
    }

    [Fact]
    public void Create_DuplicateTenant_IsRejected()
    {
        var registry = new InMemoryTenantRegistry();
        Assert.True(registry.TryCreate(new TenantRecordDto("tenant-x", "Tenant X")));
        Assert.False(registry.TryCreate(new TenantRecordDto("tenant-x", "again")));
    }

    [Fact]
    public void List_ReturnsEveryRegisteredTenant()
    {
        var registry = new InMemoryTenantRegistry(new[]
        {
            new TenantRecordDto("tenant-a", "A"),
            new TenantRecordDto("tenant-b", "B"),
        });

        var ids = registry.List().Select(t => t.TenantId).ToHashSet();
        Assert.Contains("tenant-a", ids);
        Assert.Contains("tenant-b", ids);
    }

    [Fact]
    public void Delete_RemovesTenant_AndItNoLongerResolves()
    {
        var registry = new InMemoryTenantRegistry(new[] { new TenantRecordDto("tenant-a", "A") });

        Assert.True(registry.TryDelete("tenant-a"));
        Assert.False(registry.TryResolve(new TenantId("tenant-a"), out _));
        Assert.False(registry.TryDelete("tenant-a")); // already gone
    }

    [Fact]
    public void UnknownTenant_DoesNotResolve()
    {
        var registry = new InMemoryTenantRegistry();
        Assert.False(registry.TryResolve(new TenantId("nope"), out _));
    }
}
