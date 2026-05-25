using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GameServer.Persistence.Postgres;

/// <summary>
/// Design-time factory so the EF tools (<c>dotnet ef migrations add</c>) can construct a
/// <see cref="TenantDbContext"/> WITHOUT a live database or a tenant connection resolver. The
/// connection string here is never opened at design time — it only gives the Npgsql provider a
/// shape to scaffold the migration against. At runtime, contexts are created per-tenant by
/// <see cref="TenantDbContextFactory"/> instead.
/// </summary>
public sealed class DesignTimeTenantDbContextFactory : IDesignTimeDbContextFactory<TenantDbContext>
{
    public TenantDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql("Host=localhost;Database=citadel_design_time;Username=postgres;Password=postgres")
            .Options;
        return new TenantDbContext(options);
    }
}
