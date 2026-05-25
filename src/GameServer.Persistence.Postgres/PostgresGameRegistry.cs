using GameServer.ControlPlane;
using GameServer.Replication;

namespace GameServer.Persistence.Postgres;

/// <summary>
/// Durable, database-per-tenant game/version registry. A game (and each of its versions) is persisted in
/// the owning tenant's own database (the Wave-3 <c>games</c> / <c>game_versions</c> tables), so a game
/// registered via the control-plane API survives a restart — provisioning a game is a runtime API call,
/// NOT a code change. Reads (including the realtime hot-path <see cref="GetPolicy"/>) are served from a
/// warm in-memory cache, so the hot path never makes a database round-trip.
/// </summary>
/// <remarks>
/// Honest substitute for the in-memory catalog: identical <see cref="IGameRegistry"/> contract. The cache
/// is its own (it does not reuse another ring's concrete catalog — that would be a sideways dependency); it
/// is seeded at construction from the durable rows the composition root reads once via <see cref="Load"/>,
/// so the registry's mutation logic is unit-testable without a database. The DB write-through is exercised
/// end-to-end by the Testcontainers integration scenario.
/// </remarks>
public sealed class PostgresGameRegistry : IGameRegistry
{
    private readonly ITenantDbContextFactory _contexts;
    private readonly Dictionary<string, string> _gameTenant = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GameDetail> _games = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<GameVersionDto>> _versions = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>The durable rows loaded from one tenant's database, ready to seed a registry cache.</summary>
    public sealed record Seed(string TenantId, IReadOnlyList<GameDetail> Games, IReadOnlyList<GameVersionDto> Versions);

    public PostgresGameRegistry(ITenantDbContextFactory contexts, IEnumerable<Seed> seeds)
    {
        _contexts = contexts;
        foreach (var seed in seeds)
        {
            foreach (var game in seed.Games)
            {
                _gameTenant[game.GameId] = seed.TenantId;
                _games[game.GameId] = game;
                _versions[game.GameId] = new List<GameVersionDto>();
            }

            foreach (var v in seed.Versions)
            {
                if (_versions.TryGetValue(v.GameId, out var list) && list.All(x => x.SchemaVersion != v.SchemaVersion))
                {
                    list.Add(v);
                }
            }
        }
    }

    /// <summary>
    /// Reads the durable game/version rows from each tenant's database into a set of <see cref="Seed"/>s.
    /// Called once at startup by the composition root; replication policy for a known game id is supplied by
    /// <paramref name="policyFor"/> (platform config), since policy is not persisted.
    /// </summary>
    public static IReadOnlyList<Seed> Load(
        ITenantDbContextFactory contexts,
        IEnumerable<string> tenants,
        Func<string, ReplicationPolicy?>? policyFor = null)
    {
        policyFor ??= _ => null;
        var seeds = new List<Seed>();
        foreach (var tenantId in tenants)
        {
            using var db = contexts.CreateForTenant(tenantId);
            var games = db.Games.ToList()
                .Select(g => new GameDetail(g.GameId, g.DisplayName, string.Empty, g.CurrentSchemaVersion, policyFor(g.GameId)))
                .ToList();
            var versions = db.GameVersions.ToList()
                .Select(v => new GameVersionDto(v.GameId, v.SchemaVersion, v.Notes))
                .ToList();
            seeds.Add(new Seed(tenantId, games, versions));
        }

        return seeds;
    }

    public bool TryCreateGame(string owningTenantId, GameDetail game)
    {
        lock (_gate)
        {
            if (!_games.TryAdd(game.GameId, game))
            {
                return false;
            }

            _gameTenant[game.GameId] = owningTenantId;
            _versions[game.GameId] = new List<GameVersionDto> { new(game.GameId, game.ProtocolVersion, "initial") };

            using var db = _contexts.CreateForTenant(owningTenantId);
            db.Games.Add(new GameRecord
            {
                GameId = game.GameId,
                DisplayName = game.Name,
                CurrentSchemaVersion = game.ProtocolVersion,
            });
            db.GameVersions.Add(new GameVersionRecord
            {
                GameId = game.GameId,
                SchemaVersion = game.ProtocolVersion,
                Notes = "initial",
            });
            db.SaveChanges();
            return true;
        }
    }

    public bool TryDeleteGame(string gameId)
    {
        lock (_gate)
        {
            if (!_gameTenant.TryGetValue(gameId, out var tenantId))
            {
                return false;
            }

            _games.Remove(gameId);
            _versions.Remove(gameId);
            _gameTenant.Remove(gameId);

            using var db = _contexts.CreateForTenant(tenantId);
            var game = db.Games.FirstOrDefault(g => g.GameId == gameId);
            if (game is not null)
            {
                db.Games.Remove(game);
            }

            foreach (var v in db.GameVersions.Where(v => v.GameId == gameId).ToList())
            {
                db.GameVersions.Remove(v);
            }

            db.SaveChanges();
            return true;
        }
    }

    public bool TryCreateVersion(GameVersionDto version)
    {
        lock (_gate)
        {
            if (!_gameTenant.TryGetValue(version.GameId, out var tenantId))
            {
                return false;
            }

            var list = _versions[version.GameId];
            if (list.Any(v => v.SchemaVersion == version.SchemaVersion))
            {
                return false;
            }

            list.Add(version);

            using var db = _contexts.CreateForTenant(tenantId);
            db.GameVersions.Add(new GameVersionRecord
            {
                GameId = version.GameId,
                SchemaVersion = version.SchemaVersion,
                Notes = version.Notes,
            });
            db.SaveChanges();
            return true;
        }
    }

    public bool TryGet(string gameId, out GameDetail game)
    {
        lock (_gate)
        {
            return _games.TryGetValue(gameId, out game!);
        }
    }

    public IReadOnlyList<GameSummary> List()
    {
        lock (_gate)
        {
            return _games.Values.Select(g => new GameSummary(g.GameId, g.Name)).ToList();
        }
    }

    public IReadOnlyList<GameVersionDto> ListVersions(string gameId)
    {
        lock (_gate)
        {
            return _versions.TryGetValue(gameId, out var list)
                ? list.OrderBy(v => v.SchemaVersion).ToList()
                : Array.Empty<GameVersionDto>();
        }
    }

    public ReplicationPolicy GetPolicy(string gameId)
    {
        lock (_gate)
        {
            return _games.TryGetValue(gameId, out var game) ? game.Replication ?? ReplicationPolicy.Default : ReplicationPolicy.Default;
        }
    }
}
