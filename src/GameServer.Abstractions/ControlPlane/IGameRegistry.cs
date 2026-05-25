using GameServer.Replication;

namespace GameServer.ControlPlane;

// PORTS + DTOs (rank 0). The game registry is a port (the control plane mutates it; the realtime edge
// reads its replication policy) and adapters implement it (in-memory catalog, durable database-per-tenant
// registry). It lives in the universal kernel so a rank-1 durable adapter can implement it without a
// sideways inter-ring dependency. Per the kernel rule, the namespace stays GameServer.ControlPlane.

/// <summary>A short catalog listing of a game.</summary>
public sealed record GameSummary(string GameId, string Name);

/// <summary>
/// Game catalog entry. <see cref="Replication"/> is the per-game replication policy
/// the realtime data plane applies (interest, delta, budget). Null means the
/// platform default (everyone/full, batched).
/// </summary>
public sealed record GameDetail(string GameId, string Name, string Description, int ProtocolVersion, ReplicationPolicy? Replication = null);

/// <summary>A registered version (schema version + notes) of a game.</summary>
public sealed record GameVersionDto(string GameId, int SchemaVersion, string Notes);

/// <summary>
/// The control-plane registry of games and their versions. Registering a game or a new game version is a
/// runtime API call against this registry — NOT a code change. The realtime data plane reads the same
/// instance for per-game replication policy (<see cref="GetPolicy"/>), so a game added via the API is
/// immediately usable by new rooms with no redeploy.
/// </summary>
/// <remarks>
/// A game is tenant-scoped data (the owning tenant is supplied on create), so a durable adapter persists it
/// in that tenant's own database. The read API is tenant-agnostic by game id (game ids are unique across the
/// platform) because the realtime hot path resolves replication policy by game id alone.
/// </remarks>
public interface IGameRegistry
{
    /// <summary>Registers a game owned by <paramref name="owningTenantId"/>. False if a game with that id already exists.</summary>
    bool TryCreateGame(string owningTenantId, GameDetail game);

    bool TryGet(string gameId, out GameDetail game);

    IReadOnlyList<GameSummary> List();

    bool TryDeleteGame(string gameId);

    /// <summary>Registers a new schema version for an existing game. False if the game is unknown or the version already exists.</summary>
    bool TryCreateVersion(GameVersionDto version);

    /// <summary>Lists the registered versions of a game (empty if the game is unknown).</summary>
    IReadOnlyList<GameVersionDto> ListVersions(string gameId);

    /// <summary>The per-game replication policy used on the hot path; platform default for an unknown game.</summary>
    ReplicationPolicy GetPolicy(string gameId);
}
