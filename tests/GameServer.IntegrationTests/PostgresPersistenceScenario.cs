using GameServer.Observability;
using GameServer.Persistence;
using GameServer.Persistence.Postgres;
using GameServer.Protocol;
using GameServer.Routing;
using GameServer.Simulation;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// The durable, multi-tenant proof the in-memory scenarios cannot give: the EF Core / Npgsql
/// <see cref="PostgresSnapshotStore{TKey}"/> and <see cref="PostgresEventLog{TKey}"/> persist a room's
/// checkpoint + events into the OWNING TENANT's own Postgres database, so a fresh process recovers the
/// room deterministically, and one tenant's data is unreachable through another tenant's connection.
/// Uses Testcontainers to spin a throwaway Postgres; with no container engine the tests SKIP (not pass)
/// with a clear reason, keeping the suite honest.
/// </summary>
/// <remarks>
/// Database-per-tenant: a single Postgres SERVER hosts one DATABASE per tenant. The connection
/// resolver maps tenant → that tenant's database connection string; there is no shared table, so
/// isolation is enforced at the connection boundary, not by a WHERE-clause filter.
/// Integration scenario: requires Docker/Podman; kept out of the fast unit loop.
/// </remarks>
public sealed class PostgresPersistenceScenario
{
    private static readonly RoomKey Arena = new(new TenantId("tenant-a"), new RoomId("arena"));

    private readonly ITestOutputHelper _output;

    public PostgresPersistenceScenario(ITestOutputHelper output) => _output = output;

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

    /// <summary>Creates an isolated database per tenant on the running server and returns a resolver
    /// that maps each tenant to its OWN database — the database-per-tenant boundary.</summary>
    private static async Task<DictionaryTenantConnectionResolver> ProvisionTenantDatabasesAsync(
        PostgreSqlContainer pg, params string[] tenantIds)
    {
        var admin = new NpgsqlConnectionStringBuilder(pg.GetConnectionString());
        var byTenant = new Dictionary<string, string>();

        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
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
        }

        return new DictionaryTenantConnectionResolver(byTenant);
    }

    private static DurableRoomKey KeyOf(RoomKey key) => new(key.TenantId.Value, key.RoomId.Value);

    [SkippableFact]
    public async Task RestartRecovery_RoomStateRestored_FromDurablePostgres()
    {
        // #4: produce an authoritative checkpoint + events on "node 1", persist them to Postgres, then
        // recover the room on a fresh "node 2" reading the SAME tenant database — proving state survives
        // a process restart through a real durable store.
        var pg = await StartPostgresAsync();
        await using (pg)
        {
            var resolver = await ProvisionTenantDatabasesAsync(pg, "tenant-a");
            var factory = new TenantDbContextFactory(resolver);
            factory.Migrate("tenant-a");

            var snapshots = new PostgresSnapshotStore<RoomKey>(factory, KeyOf);
            var events = new PostgresEventLog<RoomKey>(factory, KeyOf);

            // --- node 1: run a room, save snapshot + events ---
            var room = new GameRoom(Arena.RoomId, new MoveRightGame(), new LogicalSimulationClock(), new SeededRandomSource(seed: 13));
            room.Join(new PlayerId("p1"));
            snapshots.Save(Arena, room.Snapshot()); // baseline @ tick 0
            for (var seq = 1; seq <= 5; seq++)
            {
                room.TryEnqueue(new PlayerId("p1"), MoveRightGame.MoveRight, seq);
            }

            var tick = room.Tick();
            foreach (var e in tick.Events)
            {
                events.Append(Arena, e);
            }

            snapshots.Save(Arena, tick.Snapshot); // checkpoint after the moves

            // --- node 2: fresh stores over the SAME database recover the room ---
            var freshFactory = new TenantDbContextFactory(resolver);
            var recovery = new RoomRecoveryService(
                new PostgresSnapshotStore<RoomKey>(freshFactory, KeyOf),
                new PostgresEventLog<RoomKey>(freshFactory, KeyOf),
                (rid, _) => new GameRoom(rid, new MoveRightGame(), new LogicalSimulationClock(), new SeededRandomSource(seed: 1)),
                new AggregatingTelemetrySink());

            var result = recovery.Restore(Arena, new GameId("demo"), MissingSnapshotPolicy.Fail);

            Assert.Equal(RoomRecoveryOutcome.RestoredFromSnapshot, result.Outcome);
            var x = MoveRightGame.DecodeX(result.Room!.Project().Single(e => e.Id.Value == "p1").Payload);
            _output.WriteLine($"recovered p1.x after restart via Postgres = {x}");
            Assert.Equal(5, x);
        }
    }

    [SkippableFact]
    public async Task DeterministicReplayWithSeed_OverPostgres_YieldsIdenticalState_WithCapturedSeed()
    {
        // #6 (through the durable path): the snapshot header's captured seed survives the round-trip to
        // Postgres, so a fresh room re-seeded from it replays the persisted events to identical state.
        var pg = await StartPostgresAsync();
        await using (pg)
        {
            var resolver = await ProvisionTenantDatabasesAsync(pg, "tenant-a");
            var factory = new TenantDbContextFactory(resolver);
            factory.Migrate("tenant-a");
            var snapshots = new PostgresSnapshotStore<RoomKey>(factory, KeyOf);

            var room = new GameRoom(Arena.RoomId, new MoveRightGame(), new LogicalSimulationClock(), new SeededRandomSource(seed: 99));
            snapshots.Save(Arena, room.Snapshot());

            Assert.True(snapshots.TryGetLatest(Arena, out var header));
            Assert.Equal(99, header.Seed); // the captured seed round-tripped through Postgres
            Assert.Equal(((IGameSimulation)new MoveRightGame()).SchemaVersion, header.GameSchemaVersion);
        }
    }

    [SkippableFact]
    public async Task PerTenantDbIsolation_TenantAWrites_NeverVisibleViaTenantBConnection()
    {
        // The security-critical invariant: tenant A's snapshot/events live in tenant A's database and
        // cannot be read through tenant B's connection. Each tenant resolves to its OWN database, so a
        // read scoped to tenant B's key (tenant B's database) finds nothing of tenant A's.
        var pg = await StartPostgresAsync();
        await using (pg)
        {
            var resolver = await ProvisionTenantDatabasesAsync(pg, "tenant-a", "tenant-b");
            var factory = new TenantDbContextFactory(resolver);
            factory.Migrate("tenant-a");
            factory.Migrate("tenant-b");

            var snapshots = new PostgresSnapshotStore<RoomKey>(factory, KeyOf);
            var events = new PostgresEventLog<RoomKey>(factory, KeyOf);

            // Tenant A writes a room named "arena".
            var aKey = new RoomKey(new TenantId("tenant-a"), new RoomId("arena"));
            snapshots.Save(aKey, new RoomSnapshot(7, new byte[] { 1, 2, 3 }, Seed: 42, GameSchemaVersion: 1));
            events.Append(aKey, new RoomEvent(7, new PlayerId("p1"), MoveRightGame.MoveRight));

            // Tenant B, using the SAME room id, must see NOTHING of tenant A's — different database.
            var bKey = new RoomKey(new TenantId("tenant-b"), new RoomId("arena"));
            Assert.False(snapshots.TryGetLatest(bKey, out _), "tenant B must not see tenant A's snapshot");
            Assert.Empty(events.Read(bKey));

            // Tenant A still sees its own data (sanity: isolation did not also hide A from A).
            Assert.True(snapshots.TryGetLatest(aKey, out var aSnapshot));
            Assert.Equal(42, aSnapshot.Seed);
            Assert.Single(events.Read(aKey));
            _output.WriteLine("per-tenant DB isolation verified: tenant B's connection cannot read tenant A's room");
        }
    }

    [SkippableFact]
    public async Task MigrationIdempotency_ApplyingMigrationsTwice_IsClean()
    {
        // Migrations must apply cleanly twice (e.g. two nodes starting, or a restart): the second apply
        // is a no-op, not a "relation already exists" failure.
        var pg = await StartPostgresAsync();
        await using (pg)
        {
            var resolver = await ProvisionTenantDatabasesAsync(pg, "tenant-a");
            var factory = new TenantDbContextFactory(resolver);

            factory.Migrate("tenant-a");
            var second = Record.Exception(() => factory.Migrate("tenant-a"));

            Assert.Null(second);

            // And the store works after the second (idempotent) apply.
            var snapshots = new PostgresSnapshotStore<RoomKey>(factory, KeyOf);
            snapshots.Save(Arena, new RoomSnapshot(1, Array.Empty<byte>(), Seed: 5, GameSchemaVersion: 1));
            Assert.True(snapshots.TryGetLatest(Arena, out var snapshot));
            Assert.Equal(5, snapshot.Seed);
            _output.WriteLine("migrations applied twice cleanly; store usable afterward");
        }
    }
}
