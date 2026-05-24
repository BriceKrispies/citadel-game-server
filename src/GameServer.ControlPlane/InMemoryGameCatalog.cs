using GameServer.Replication;

namespace GameServer.ControlPlane;

/// <summary>
/// In-memory game catalog for the control plane. Seeds a few demo games that differ
/// only by replication policy, so the realtime data plane's per-game behavior
/// (full/batched vs delta vs budget) is driven entirely by catalog config. Honest
/// substitute for a future catalog backing store; owns no simulation state.
/// </summary>
public sealed class InMemoryGameCatalog
{
    private readonly Dictionary<string, GameDetail> _games;

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

    public InMemoryGameCatalog(IEnumerable<GameDetail> games) =>
        _games = games.ToDictionary(g => g.GameId, StringComparer.Ordinal);

    public IReadOnlyList<GameSummary> List() =>
        _games.Values.Select(g => new GameSummary(g.GameId, g.Name)).ToList();

    public bool TryGet(string gameId, out GameDetail game) => _games.TryGetValue(gameId, out game!);

    /// <summary>The replication policy for a game, or the platform default if unknown/unset.</summary>
    public ReplicationPolicy GetPolicy(string gameId) =>
        _games.TryGetValue(gameId, out var game) ? game.Replication ?? ReplicationPolicy.Default : ReplicationPolicy.Default;
}
