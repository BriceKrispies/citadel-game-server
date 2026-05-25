using Xunit;

namespace GameServer.ControlPlane;

public sealed class AuditRecordTests
{
    [Fact]
    public void TenantId_DefaultsToNull_ForPlatformLevelActions()
    {
        // A platform-level action (no tenant scope) carries no tenant: the durable store routes it to no
        // per-tenant database. The default keeps existing call sites (5-arg) source-compatible.
        var record = new AuditRecord("admin", "drain-node", "node-1", DateTimeOffset.UnixEpoch, "allowed");

        Assert.Null(record.TenantId);
    }

    [Fact]
    public void TenantId_CarriesTheScopedTenant_WhenProvided()
    {
        var record = new AuditRecord("admin", "create-game", "tenant-a/g", DateTimeOffset.UnixEpoch, "allowed", "tenant-a");

        Assert.Equal("tenant-a", record.TenantId);
    }
}
