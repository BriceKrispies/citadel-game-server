using Microsoft.EntityFrameworkCore;

namespace GameServer.Persistence.Postgres;

/// <summary>
/// Creates a <see cref="TenantDbContext"/> bound to a SPECIFIC tenant's database. Every durable read
/// or write goes through here, so the database-per-tenant boundary is the single place isolation is
/// enforced: a context is only ever opened against the connection the resolver returns for the
/// requested tenant, and that connection reaches only that tenant's database.
/// </summary>
public interface ITenantDbContextFactory
{
    /// <summary>Opens a context against <paramref name="tenantId"/>'s database.</summary>
    TenantDbContext CreateForTenant(string tenantId);

    /// <summary>
    /// Ensures the tenant's database schema exists by applying the migrations. Idempotent: applying
    /// when already current is a no-op, so calling it on every startup (or twice) is safe.
    /// </summary>
    void Migrate(string tenantId);
}

/// <summary>
/// Default factory: resolves the tenant's connection string and opens an Npgsql-backed context on it.
/// </summary>
public sealed class TenantDbContextFactory : ITenantDbContextFactory
{
    private readonly ITenantConnectionResolver _connections;

    public TenantDbContextFactory(ITenantConnectionResolver connections) => _connections = connections;

    public TenantDbContext CreateForTenant(string tenantId)
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(_connections.ConnectionStringFor(tenantId))
            .Options;
        return new TenantDbContext(options);
    }

    public void Migrate(string tenantId)
    {
        using var db = CreateForTenant(tenantId);
        db.Database.Migrate();
    }
}
