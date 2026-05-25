using GameServer.ControlPlane;
using GameServer.Persistence.Postgres;
using GameServer.Replication;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// The durable control-plane proof the in-memory authz scenarios cannot give: a tenant-scoped audited
/// action (and the durable game registry) writes into the OWNING tenant's own Postgres database — so the
/// audit trail and game catalog are isolated per tenant exactly like the rest of a tenant's data, with no
/// shared table for one tenant to read another's. Testcontainers Postgres; SKIPs (not passes) with no
/// container engine.
/// </summary>
/// <remarks>Integration scenario: requires Docker/Podman; kept out of the fast unit loop.</remarks>
public sealed class DurableControlPlaneScenario
{
    private readonly ITestOutputHelper _output;

    public DurableControlPlaneScenario(ITestOutputHelper output) => _output = output;

    private static async Task<PostgreSqlContainer> StartPostgresAsync()
    {
        try
        {
            var pg = new PostgreSqlBuilder("postgres:16-alpine").Build();
            await pg.StartAsync();
            return pg;
        }
        catch (Exception ex)
        {
            throw new SkipException($"Docker/Postgres not available in this environment: {ex.Message}");
        }
    }

    private static async Task<DictionaryTenantConnectionResolver> ProvisionTenantDatabasesAsync(
        PostgreSqlContainer pg, params string[] tenantIds)
    {
        var admin = new NpgsqlConnectionStringBuilder(pg.GetConnectionString());
        var byTenant = new Dictionary<string, string>();

        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync();
        foreach (var tenantId in tenantIds)
        {
            var database = "db_" + tenantId.Replace('-', '_');
            await using (var cmd = new NpgsqlCommand($"CREATE DATABASE \"{database}\";", connection))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            var tenantConn = new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Database = database };
            byTenant[tenantId] = tenantConn.ConnectionString;
        }

        return new DictionaryTenantConnectionResolver(byTenant);
    }

    [SkippableFact]
    public async Task DurableAudit_WritesToOwningTenantDatabase_AndIsIsolatedFromOtherTenant()
    {
        var pg = await StartPostgresAsync();
        await using (pg)
        {
            var resolver = await ProvisionTenantDatabasesAsync(pg, "tenant-a", "tenant-b");
            var factory = new TenantDbContextFactory(resolver);
            factory.Migrate("tenant-a");
            factory.Migrate("tenant-b");

            var audit = new PostgresAuditLog(factory);

            // A tenant-a action and a tenant-b action, plus a platform-level (tenant-less) action.
            audit.Record(new AuditRecord("op-a", "create-game", "tenant-a/g", DateTimeOffset.UtcNow, "allowed", "tenant-a"));
            audit.Record(new AuditRecord("op-a", "terminate-room", "tenant-a/r", DateTimeOffset.UtcNow, "denied", "tenant-a"));
            audit.Record(new AuditRecord("op-b", "create-game", "tenant-b/g", DateTimeOffset.UtcNow, "allowed", "tenant-b"));
            audit.Record(new AuditRecord("admin", "drain-node", "node-1", DateTimeOffset.UtcNow, "allowed")); // platform-level

            // tenant-a's database holds ONLY tenant-a's two actions (the denial is recorded too).
            var durableA = audit.ReadDurable("tenant-a");
            Assert.Equal(2, durableA.Count);
            Assert.Contains(durableA, r => r.Action == "create-game" && r.Outcome == "allowed");
            Assert.Contains(durableA, r => r.Action == "terminate-room" && r.Outcome == "denied");
            // No tenant-b action leaked into tenant-a's database.
            Assert.DoesNotContain(durableA, r => r.Target.StartsWith("tenant-b"));

            // tenant-b's database holds ONLY tenant-b's single action.
            var durableB = audit.ReadDurable("tenant-b");
            Assert.Single(durableB);
            Assert.Equal("op-b", durableB[0].Actor);

            // The in-memory mirror holds every record (incl. the platform-level one) for Read().
            Assert.Equal(4, audit.Read().Count);

            _output.WriteLine("durable audit: tenant-a db has 2 rows (incl. denial), tenant-b db has 1, no cross-tenant leak");
        }
    }

    [SkippableFact]
    public async Task DurableGameRegistry_PersistsGame_AndReloadsAfterRestart()
    {
        var pg = await StartPostgresAsync();
        await using (pg)
        {
            var resolver = await ProvisionTenantDatabasesAsync(pg, "tenant-a");
            var factory = new TenantDbContextFactory(resolver);
            factory.Migrate("tenant-a");

            // "Node 1": register a durable game + a version via the registry.
            var registry1 = new PostgresGameRegistry(factory, PostgresGameRegistry.Load(factory, new[] { "tenant-a" }));
            Assert.True(registry1.TryCreateGame("tenant-a",
                new GameDetail("durable-g", "Durable", "", 1, ReplicationPolicy.Default with { SnapshotMode = SnapshotMode.Delta })));
            Assert.True(registry1.TryCreateVersion(new GameVersionDto("durable-g", 2, "v2")));

            // "Node 2": a fresh registry loads from the SAME tenant database — the game survives a restart,
            // proving provisioning is durable (not just in-process state).
            ReplicationPolicy? PolicyFor(string id) => id == "durable-g" ? ReplicationPolicy.Default with { SnapshotMode = SnapshotMode.Delta } : null;
            var registry2 = new PostgresGameRegistry(factory, PostgresGameRegistry.Load(factory, new[] { "tenant-a" }, PolicyFor));

            Assert.True(registry2.TryGet("durable-g", out var game));
            Assert.Equal("Durable", game.Name);
            Assert.Equal(SnapshotMode.Delta, registry2.GetPolicy("durable-g").SnapshotMode);
            Assert.Contains(registry2.ListVersions("durable-g"), v => v.SchemaVersion == 2);

            _output.WriteLine("durable game registry: game + v2 persisted to tenant-a db and reloaded on a fresh registry");
        }
    }
}
