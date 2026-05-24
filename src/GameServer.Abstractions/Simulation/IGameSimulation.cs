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
    /// <summary>Initializes state for a newly joined player. Idempotent.</summary>
    void Join(PlayerId player);

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

    /// <summary>Serializes all authoritative state into an opaque durable snapshot.</summary>
    byte[] Serialize();

    /// <summary>Restores authoritative state from a previously <see cref="Serialize"/>d snapshot.</summary>
    void Restore(byte[] state);
}
