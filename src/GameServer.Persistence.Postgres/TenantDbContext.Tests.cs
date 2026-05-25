using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GameServer.Persistence.Postgres;

/// <summary>Hermetic model-metadata checks: building the EF model does not open a connection, so
/// these run with no database. They lock in the per-tenant table mapping and keys.</summary>
public sealed class TenantDbContextTests
{
    private static TenantDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            // A connection string is required to scaffold the model but is never opened here.
            .UseNpgsql("Host=unused;Database=unused;Username=unused;Password=unused")
            .Options;
        return new TenantDbContext(options);
    }

    [Fact]
    public void Maps_AllDurableRecordTables()
    {
        using var db = NewContext();
        var tables = db.Model.GetEntityTypes().Select(e => e.GetTableName()).ToHashSet();

        Assert.Contains("games", tables);
        Assert.Contains("game_versions", tables);
        Assert.Contains("rooms", tables);
        Assert.Contains("sessions", tables);
        Assert.Contains("audit_records", tables);
        Assert.Contains("room_snapshots", tables);
        Assert.Contains("room_events", tables);
    }

    [Fact]
    public void RoomSnapshot_IsKeyedByRoomId_SoSaveIsAnUpsert()
    {
        using var db = NewContext();
        var key = db.Model.FindEntityType(typeof(RoomSnapshotRecord))!.FindPrimaryKey()!;

        Assert.Equal(new[] { nameof(RoomSnapshotRecord.RoomId) }, key.Properties.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void RoomEvent_IsKeyedBy_Room_Tick_Ordinal_ForOrderedAppend()
    {
        using var db = NewContext();
        var key = db.Model.FindEntityType(typeof(RoomEventRecord))!.FindPrimaryKey()!;

        Assert.Equal(
            new[] { nameof(RoomEventRecord.RoomId), nameof(RoomEventRecord.Tick), nameof(RoomEventRecord.Ordinal) },
            key.Properties.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void HasNo_TenantIdColumn_BecauseIsolationIsPerDatabase()
    {
        // The isolation invariant in metadata form: NO entity carries a tenant discriminator, because
        // there is no shared table — each tenant's rows live in that tenant's own database.
        using var db = NewContext();
        foreach (var entity in db.Model.GetEntityTypes())
        {
            Assert.DoesNotContain(entity.GetProperties(), p =>
                p.Name.Contains("Tenant", StringComparison.OrdinalIgnoreCase));
        }
    }
}
