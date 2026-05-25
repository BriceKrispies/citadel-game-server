using GameServer.Replication;

namespace GameServer.ControlPlane;

/// <summary>
/// In-memory game catalog and registry for the control plane. Seeds a few demo games that differ
/// only by replication policy, so the realtime data plane's per-game behavior
/// (full/batched vs delta vs budget) is driven entirely by catalog config. Honest
/// substitute for a future catalog backing store; owns no simulation state.
/// </summary>
/// <remarks>
/// Implements <see cref="IGameRegistry"/>: the control-plane CRUD endpoints mutate this instance and the
/// realtime edge reads it for replication policy, so a game (or game version) registered via the API is
/// usable by new rooms with NO code change. Mutations are serialized so concurrent admin writes and
/// hot-path policy reads stay consistent.
/// </remarks>
public sealed class InMemoryGameCatalog : IGameRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, GameDetail> _games;
    private readonly Dictionary<string, List<GameVersionDto>> _versions = new(StringComparer.Ordinal);

    public InMemoryGameCatalog()
        : this(new[]
        {
            new GameDetail("demo-game", "Demo Game", "MoveRight demo — full snapshots, batched one-per-client.", 1,
                ReplicationPolicy.Default),
            new GameDetail("demo-delta", "Demo Game (delta)", "Delta replication — idle/unchanged state costs ~nothing.", 1,
                ReplicationPolicy.Default with { SnapshotMode = SnapshotMode.Delta }),
            new GameDetail("demo-budget", "Demo Game (budget)", "Full snapshots capped to a per-client byte budget.", 1,
                ReplicationPolicy.Default with { PerClientBudgetBytes = 256 }),
        })
    {
    }

    public InMemoryGameCatalog(IEnumerable<GameDetail> games)
    {
        _games = games.ToDictionary(g => g.GameId, StringComparer.Ordinal);
        foreach (var game in _games.Values)
        {
            _versions[game.GameId] = new List<GameVersionDto>
            {
                new(game.GameId, game.ProtocolVersion, "seeded"),
            };
        }
    }

    public bool TryCreateGame(string owningTenantId, GameDetail game)
    {
        // The in-memory catalog is platform-global, so it does not partition by tenant; the owning tenant
        // is accepted for contract parity with the durable (database-per-tenant) registry.
        _ = owningTenantId;
        lock (_gate)
        {
            if (!_games.TryAdd(game.GameId, game))
            {
                return false;
            }

            _versions[game.GameId] = new List<GameVersionDto>
            {
                new(game.GameId, game.ProtocolVersion, "initial"),
            };
            return true;
        }
    }

    public bool TryDeleteGame(string gameId)
    {
        lock (_gate)
        {
            _versions.Remove(gameId);
            return _games.Remove(gameId);
        }
    }

    public bool TryCreateVersion(GameVersionDto version)
    {
        lock (_gate)
        {
            if (!_games.ContainsKey(version.GameId))
            {
                return false;
            }

            var versions = _versions.TryGetValue(version.GameId, out var existing)
                ? existing
                : _versions[version.GameId] = new List<GameVersionDto>();

            if (versions.Any(v => v.SchemaVersion == version.SchemaVersion))
            {
                return false;
            }

            versions.Add(version);
            return true;
        }
    }

    public IReadOnlyList<GameVersionDto> ListVersions(string gameId)
    {
        lock (_gate)
        {
            return _versions.TryGetValue(gameId, out var versions)
                ? versions.OrderBy(v => v.SchemaVersion).ToList()
                : Array.Empty<GameVersionDto>();
        }
    }

    public IReadOnlyList<GameSummary> List()
    {
        lock (_gate)
        {
            return _games.Values.Select(g => new GameSummary(g.GameId, g.Name)).ToList();
        }
    }

    public bool TryGet(string gameId, out GameDetail game)
    {
        lock (_gate)
        {
            return _games.TryGetValue(gameId, out game!);
        }
    }

    /// <summary>The replication policy for a game, or the platform default if unknown/unset.</summary>
    public ReplicationPolicy GetPolicy(string gameId)
    {
        lock (_gate)
        {
            return _games.TryGetValue(gameId, out var game) ? game.Replication ?? ReplicationPolicy.Default : ReplicationPolicy.Default;
        }
    }
}
