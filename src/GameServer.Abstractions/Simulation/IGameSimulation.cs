using GameServer.Protocol;
using GameServer.Replication;

namespace GameServer.Simulation;

/// <summary>
/// The pluggable game contract — the one thing a game author implements. A game owns
/// its authoritative state, its command vocabulary, how commands mutate state, how
/// state projects into the generic replication model, and how state serializes for
/// durable snapshots/recovery.
/// </summary>
/// <remarks>
/// Everything else — connections, auth, command sequencing, snapshot/delta/ack,
/// fan-out, persistence, recovery, telemetry — is the platform's job. The platform
/// treats game state as opaque: it only ever sees entities with an id, a version, a
/// relevance key, and payload bytes. Implementations MUST be deterministic (no
/// wall-clock time, no ambient randomness) so rooms replay and test identically.
/// </remarks>
public interface IGameSimulation
{
    /// <summary>
    /// Whether <paramref name="player"/> may join the room right now. The host calls this
    /// before <see cref="Join"/>; a <c>false</c> result rejects the join with a typed
    /// <c>ServerError</c> and the player is never admitted (no membership, no state).
    /// </summary>
    /// <remarks>
    /// Default is permissive (always allow), so a game that does not gate joins behaves
    /// exactly as before (Liskov). A game implements this to enforce its own capacity,
    /// ban list, lobby-phase, or team-balance rules — never the platform's (those run
    /// earlier, at the edge).
    /// </remarks>
    bool CanJoin(PlayerId player) => true;

    /// <summary>Initializes state for a newly joined player. Idempotent.</summary>
    void Join(PlayerId player);

    /// <summary>
    /// Notifies the game that <paramref name="player"/> has left the room (clean leave or
    /// disconnect). The game may release that player's authoritative state. Default is a
    /// no-op so a game that does not care about leaves behaves exactly as before.
    /// </summary>
    void OnLeave(PlayerId player)
    {
    }

    /// <summary>
    /// The server→game signal that the room is being torn down (reaped after its last
    /// player left, or shut down). The game may flush/finalize. Called once; after it the
    /// game instance is discarded. Default is a no-op (Liskov: existing games unaffected).
    /// </summary>
    void OnTerminate()
    {
    }

    /// <summary>True once the player has been admitted via <see cref="Join"/>.</summary>
    bool HasPlayer(PlayerId player);

    /// <summary>Whether <paramref name="command"/> is a legal, known command for this player now.</summary>
    bool CanAccept(PlayerId player, string command);

    /// <summary>
    /// Applies one already-admitted command to authoritative state (the host has
    /// already checked membership, sequence, and <see cref="CanAccept"/>). The
    /// affected entity's projected <see cref="EntitySnapshot.Version"/> must change so
    /// the replication layer can detect the delta.
    /// </summary>
    void Apply(PlayerId player, string command);

    /// <summary>Projects current authoritative state into the generic replication model.</summary>
    IReadOnlyList<EntitySnapshot> Project();

    /// <summary>
    /// The version of this game's serialized-state layout. Captured into a room snapshot so a
    /// restore can detect a snapshot produced by an incompatible game build rather than silently
    /// mis-deserializing it. Default is 1; a game bumps it when it changes its <see cref="Serialize"/>
    /// format. Default interface member, so existing games are unaffected (Liskov).
    /// </summary>
    int SchemaVersion => 1;

    /// <summary>Serializes all authoritative state into an opaque durable snapshot.</summary>
    byte[] Serialize();

    /// <summary>Restores authoritative state from a previously <see cref="Serialize"/>d snapshot.</summary>
    void Restore(byte[] state);
}
