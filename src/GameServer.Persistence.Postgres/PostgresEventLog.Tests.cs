using GameServer.Protocol;
using GameServer.Simulation;
using Xunit;

namespace GameServer.Persistence.Postgres;

/// <summary>
/// Hermetic routing checks (no database): the event log projects the caller's key through the
/// selector and opens a context against the resulting tenant. Real append/read/truncate behavior is
/// proven in the Testcontainers-gated integration scenario.
/// </summary>
public sealed class PostgresEventLogTests
{
    private sealed record TestKey(string Tenant, string Room);

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

    private static PostgresEventLog<TestKey> NewLog(RecordingFactory factory) =>
        new(factory, k => new DurableRoomKey(k.Tenant, k.Room));

    [Fact]
    public void Append_RoutesToTheKeysTenantDatabase()
    {
        var factory = new RecordingFactory();
        Assert.Throws<RoutedException>(() =>
            NewLog(factory).Append(new TestKey("tenant-a", "arena"), new RoomEvent(1, new PlayerId("p1"), "MoveRight")));
        Assert.Equal("tenant-a", factory.RequestedTenant);
    }

    [Fact]
    public void Read_RoutesToTheKeysTenantDatabase()
    {
        var factory = new RecordingFactory();
        Assert.Throws<RoutedException>(() => NewLog(factory).Read(new TestKey("tenant-b", "arena")));
        Assert.Equal("tenant-b", factory.RequestedTenant);
    }

    [Fact]
    public void TruncateThrough_RoutesToTheKeysTenantDatabase()
    {
        var factory = new RecordingFactory();
        Assert.Throws<RoutedException>(() => NewLog(factory).TruncateThrough(new TestKey("tenant-c", "arena"), 10));
        Assert.Equal("tenant-c", factory.RequestedTenant);
    }
}
