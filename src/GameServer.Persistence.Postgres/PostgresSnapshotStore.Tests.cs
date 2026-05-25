using GameServer.Simulation;
using Xunit;

namespace GameServer.Persistence.Postgres;

/// <summary>
/// Hermetic routing checks (no database): proves the store projects the caller's opaque key through
/// the supplied selector and opens a context against the RESULTING TENANT — the per-tenant isolation
/// routing. Real read/write behavior is proven in the Testcontainers-gated integration scenario.
/// </summary>
public sealed class PostgresSnapshotStoreTests
{
    private sealed record TestKey(string Tenant, string Room);

    /// <summary>Records the tenant a context was requested for, then short-circuits with a sentinel so
    /// no real database is touched.</summary>
    private sealed class RecordingFactory : ITenantDbContextFactory
    {
        public string? RequestedTenant { get; private set; }

        public TenantDbContext CreateForTenant(string tenantId)
        {
            RequestedTenant = tenantId;
            throw new RoutedException();
        }

        public void Migrate(string tenantId) => throw new NotSupportedException();
    }

    private sealed class RoutedException : Exception;

    [Fact]
    public void Save_RoutesToTheKeysTenantDatabase()
    {
        var factory = new RecordingFactory();
        var store = new PostgresSnapshotStore<TestKey>(factory, k => new DurableRoomKey(k.Tenant, k.Room));

        Assert.Throws<RoutedException>(() => store.Save(new TestKey("tenant-a", "arena"), new RoomSnapshot(0, Array.Empty<byte>())));
        Assert.Equal("tenant-a", factory.RequestedTenant);
    }

    [Fact]
    public void TryGetLatest_RoutesToTheKeysTenantDatabase()
    {
        var factory = new RecordingFactory();
        var store = new PostgresSnapshotStore<TestKey>(factory, k => new DurableRoomKey(k.Tenant, k.Room));

        Assert.Throws<RoutedException>(() => store.TryGetLatest(new TestKey("tenant-b", "arena"), out _));
        Assert.Equal("tenant-b", factory.RequestedTenant);
    }
}
