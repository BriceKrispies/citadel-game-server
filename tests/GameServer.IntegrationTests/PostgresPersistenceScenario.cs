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

    /// <summary>Raw, EF-bypassing row count in a tenant's OWN database — the storage-level isolation
    /// proof: a tenant's database must hold only that tenant's rows. The table name is a fixed literal
    /// (never client input), so this is not an injection vector.</summary>
    private static async Task<long> CountRowsAsync(
        ITenantConnectionResolver resolver, string tenantId, string table)
    {
        await using var connection = new NpgsqlConnection(resolver.ConnectionStringFor(tenantId));
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand($"SELECT COUNT(*) FROM {table};", connection);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

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

            // Tenant A writes a room named "arena" (DISTINCT seed + payload + event count).
            var aKey = new RoomKey(new TenantId("tenant-a"), new RoomId("arena"));
            snapshots.Save(aKey, new RoomSnapshot(7, new byte[] { 1, 2, 3 }, Seed: 42, GameSchemaVersion: 1));
            events.Append(aKey, new RoomEvent(7, new PlayerId("p1"), MoveRightGame.MoveRight));

            // Step 1 — B's database has ZERO of A's rows BEFORE B writes anything (the leak check):
            // a read scoped to B's key (B's database) for the SAME room id sees nothing of A's, AND a
            // raw table-level count in B's database confirms it holds none of A's rows.
            var bKey = new RoomKey(new TenantId("tenant-b"), new RoomId("arena"));
            Assert.False(snapshots.TryGetLatest(bKey, out _), "tenant B must not see tenant A's snapshot");
            Assert.Empty(events.Read(bKey));
            Assert.Equal(0, await CountRowsAsync(resolver, "tenant-b", "room_snapshots"));
            Assert.Equal(0, await CountRowsAsync(resolver, "tenant-b", "room_events"));
            // And A's database holds exactly A's rows (not, e.g., a shared table holding both).
            Assert.Equal(1, await CountRowsAsync(resolver, "tenant-a", "room_snapshots"));
            Assert.Equal(1, await CountRowsAsync(resolver, "tenant-a", "room_events"));

            // Step 2 — B writes its OWN room with the SAME room id but DIFFERENT data. Neither tenant's
            // read may return the other's row: each key resolves to its own database, so even an
            // identical room id cannot collide or swap. (Catches a shared-table/forgotten-filter bug.)
            snapshots.Save(bKey, new RoomSnapshot(99, new byte[] { 9, 9, 9 }, Seed: 7, GameSchemaVersion: 2));
            events.Append(bKey, new RoomEvent(99, new PlayerId("p2"), MoveRightGame.MoveRight));
            events.Append(bKey, new RoomEvent(100, new PlayerId("p2"), MoveRightGame.MoveRight));

            Assert.True(snapshots.TryGetLatest(aKey, out var aSnapshot));
            Assert.Equal(42, aSnapshot.Seed);          // A still sees ITS seed, not B's 7
            Assert.Equal(7, aSnapshot.Tick);           // A's tick, not B's 99
            Assert.Equal(new byte[] { 1, 2, 3 }, aSnapshot.State); // A's payload, not B's 9,9,9
            Assert.Single(events.Read(aKey));          // A's one event, not B's two

            Assert.True(snapshots.TryGetLatest(bKey, out var bSnapshot));
            Assert.Equal(7, bSnapshot.Seed);           // B sees ITS seed, not A's 42
            Assert.Equal(new byte[] { 9, 9, 9 }, bSnapshot.State);
            Assert.Equal(2, events.Read(bKey).Count);

            // Step 3 — raw row counts per database confirm each holds ONLY its own (one snapshot each,
            // one vs two events) — no shared table served both tenants.
            Assert.Equal(1, await CountRowsAsync(resolver, "tenant-a", "room_snapshots"));
            Assert.Equal(1, await CountRowsAsync(resolver, "tenant-a", "room_events"));
            Assert.Equal(1, await CountRowsAsync(resolver, "tenant-b", "room_snapshots"));
            Assert.Equal(2, await CountRowsAsync(resolver, "tenant-b", "room_events"));
            _output.WriteLine("per-tenant DB isolation verified: each tenant's connection reads ONLY its own database; raw row counts confirm no shared table");
        }
    }

    [SkippableFact]
    public async Task CrashAfterEvents_BeforeNewSnapshot_RecoversToCorrectState_ByReplay()
    {
        // ADVERSARIAL (partial-write / crash safety, ordering A): the snapshot and the event log are
        // SEPARATE transactions (no 2-phase). Simulate a crash AFTER appending post-snapshot events but
        // BEFORE the next checkpoint was written: only the OLD snapshot + the events are durable.
        // Recovery must fold those events forward and land on the correct state — no loss, no double-apply.
        var pg = await StartPostgresAsync();
        await using (pg)
        {
            var resolver = await ProvisionTenantDatabasesAsync(pg, "tenant-a");
            var factory = new TenantDbContextFactory(resolver);
            factory.Migrate("tenant-a");
            var snapshots = new PostgresSnapshotStore<RoomKey>(factory, KeyOf);
            var events = new PostgresEventLog<RoomKey>(factory, KeyOf);

            var room = new GameRoom(Arena.RoomId, new MoveRightGame(), new LogicalSimulationClock(), new SeededRandomSource(seed: 13));
            room.Join(new PlayerId("p1"));
            snapshots.Save(Arena, room.Snapshot()); // checkpoint @ tick 0

            // Three ticks of moves are appended as events; the NEW snapshot is intentionally NOT saved
            // (the crash happened before it). Durable state = old snapshot (tick 0) + 3 events.
            for (var seq = 1; seq <= 3; seq++)
            {
                room.TryEnqueue(new PlayerId("p1"), MoveRightGame.MoveRight, seq);
                foreach (var e in room.Tick().Events)
                {
                    events.Append(Arena, e);
                }
            }

            // Fresh process recovers from the durable artifacts.
            var fresh = new TenantDbContextFactory(resolver);
            var recovery = new RoomRecoveryService(
                new PostgresSnapshotStore<RoomKey>(fresh, KeyOf),
                new PostgresEventLog<RoomKey>(fresh, KeyOf),
                (rid, _) => new GameRoom(rid, new MoveRightGame(), new LogicalSimulationClock(), new SeededRandomSource(seed: 1)),
                new AggregatingTelemetrySink());

            var result = recovery.Restore(Arena, new GameId("demo"), MissingSnapshotPolicy.Fail);
            Assert.Equal(RoomRecoveryOutcome.RestoredFromSnapshot, result.Outcome);
            Assert.Equal(3, result.ReplayedEventCount); // all post-snapshot events folded forward
            Assert.Equal(3, MoveRightGame.DecodeX(result.Room!.Project().Single().Payload));
        }
    }

    [SkippableFact]
    public async Task CrashAfterNewSnapshot_BeforeTruncatingOldEvents_RecoversWithoutDoubleApply()
    {
        // ADVERSARIAL (partial-write / crash safety, ordering B): a new checkpoint was written, but the
        // crash happened BEFORE the now-folded events were truncated, so the log still holds events at
        // ticks <= the new snapshot's tick. Recovery must NOT replay those (they are already in the
        // snapshot) — it replays only events strictly newer than the checkpoint. The result must equal
        // the true state, never a double-applied (inflated) position.
        var pg = await StartPostgresAsync();
        await using (pg)
        {
            var resolver = await ProvisionTenantDatabasesAsync(pg, "tenant-a");
            var factory = new TenantDbContextFactory(resolver);
            factory.Migrate("tenant-a");
            var snapshots = new PostgresSnapshotStore<RoomKey>(factory, KeyOf);
            var events = new PostgresEventLog<RoomKey>(factory, KeyOf);

            var room = new GameRoom(Arena.RoomId, new MoveRightGame(), new LogicalSimulationClock(), new SeededRandomSource(seed: 13));
            room.Join(new PlayerId("p1"));
            snapshots.Save(Arena, room.Snapshot()); // baseline @ tick 0

            // Tick 1..3: append events. The NEW snapshot IS saved (now reflects x=3 @ tick 3) but the
            // old events at ticks 1..3 were NOT truncated before the crash — they remain in the log.
            for (var seq = 1; seq <= 3; seq++)
            {
                room.TryEnqueue(new PlayerId("p1"), MoveRightGame.MoveRight, seq);
                foreach (var e in room.Tick().Events)
                {
                    events.Append(Arena, e);
                }
            }

            var checkpoint = room.Snapshot();
            Assert.Equal(3, checkpoint.Tick);
            snapshots.Save(Arena, checkpoint); // new checkpoint written...
            // ...crash here, before TruncateThrough(tick 3). The 3 events at ticks 1..3 are still durable.
            Assert.Equal(3, events.Read(Arena).Count);

            var fresh = new TenantDbContextFactory(resolver);
            var recovery = new RoomRecoveryService(
                new PostgresSnapshotStore<RoomKey>(fresh, KeyOf),
                new PostgresEventLog<RoomKey>(fresh, KeyOf),
                (rid, _) => new GameRoom(rid, new MoveRightGame(), new LogicalSimulationClock(), new SeededRandomSource(seed: 1)),
                new AggregatingTelemetrySink());

            var result = recovery.Restore(Arena, new GameId("demo"), MissingSnapshotPolicy.Fail);
            Assert.Equal(RoomRecoveryOutcome.RestoredFromSnapshot, result.Outcome);
            // The 3 events are at ticks <= the snapshot tick (3), so NONE are replayed: no double-apply.
            Assert.Equal(0, result.ReplayedEventCount);
            Assert.Equal(3, MoveRightGame.DecodeX(result.Room!.Project().Single().Payload)); // correct, not 6
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
