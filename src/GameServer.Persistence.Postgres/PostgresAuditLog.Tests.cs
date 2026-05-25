using GameServer.ControlPlane;
using Xunit;

namespace GameServer.Persistence.Postgres;

/// <summary>
/// Hermetic checks of the durable audit log's routing and mirror, with NO database. The actual durable
/// write into a tenant's <c>audit_records</c> table is exercised end-to-end by the Testcontainers
/// integration scenario; here we prove the in-process decisions: platform-level (tenant-less) records never
/// touch a tenant database, every record is mirrored for <see cref="IAuditLog.Read"/>, and a tenant-scoped
/// record routes to that tenant's context.
/// </summary>
public sealed class PostgresAuditLogTests
{
    private sealed class RecordingFactory : ITenantDbContextFactory
    {
        public List<string> Opened { get; } = new();

        public TenantDbContext CreateForTenant(string tenantId)
        {
            Opened.Add(tenantId);
            // The hermetic tests never drive a record that would call this with a write; throwing here
            // proves the platform-level path never opens a tenant database.
            throw new InvalidOperationException("hermetic: no database available");
        }

        public void Migrate(string tenantId) => throw new NotSupportedException();
    }

    [Fact]
    public void PlatformLevelRecord_NeverOpensATenantDatabase_ButIsMirrored()
    {
        var factory = new RecordingFactory();
        var audit = new PostgresAuditLog(factory);

        // TenantId null => platform-level; must NOT route to a per-tenant database.
        audit.Record(new AuditRecord("admin", "drain-node", "node-1", DateTimeOffset.UnixEpoch, "allowed"));

        Assert.Empty(factory.Opened);
        Assert.Single(audit.Read());
        Assert.Equal("drain-node", audit.Read()[0].Action);
    }

    [Fact]
    public void TenantScopedRecord_RoutesToThatTenantsContext()
    {
        var factory = new RecordingFactory();
        var audit = new PostgresAuditLog(factory);

        // The durable write is unavailable here (factory throws), but routing to the tenant's context is
        // attempted — proving the database-per-tenant routing decision is keyed off the record's tenant.
        var ex = Record.Exception(() =>
            audit.Record(new AuditRecord("op", "create-game", "tenant-a/g", DateTimeOffset.UnixEpoch, "allowed", "tenant-a")));

        Assert.NotNull(ex);
        Assert.Equal(new[] { "tenant-a" }, factory.Opened);
        // The action was mirrored BEFORE the durable write was attempted, so the audit trail survives a
        // durable-write failure.
        Assert.Single(audit.Read());
    }
}
