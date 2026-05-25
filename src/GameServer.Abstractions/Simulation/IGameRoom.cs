using GameServer.Protocol;
using GameServer.Replication;

namespace GameServer.Simulation;

/// <summary>
/// An authoritative, actor-like room host. Only the room mutates its own state, and
/// only on a tick. External systems enqueue intent and observe results; they never
/// reach in and change state directly. The room is game-agnostic: it owns command
/// sequencing and the tick loop, and delegates state/rules to an
/// <see cref="IGameSimulation"/>.
/// </summary>
public interface IGameRoom
{
    RoomId Id { get; }

    /// <summary>
    /// Commands currently queued for the next tick. The data plane observes this as a
    /// backpressure gauge; it rises when clients outrun the tick drain rate.
    /// </summary>
    int QueueDepth { get; }

    /// <summary>
    /// Whether the room's game will accept <paramref name="player"/> right now. The data
    /// plane calls this before <see cref="Join"/>; a false result rejects the join. Defers
    /// to the game's <c>CanJoin</c> rule (default permissive).
    /// </summary>
    bool CanJoin(PlayerId player);

    /// <summary>Admits a player to the room's game. Idempotent for an already-joined player.</summary>
    void Join(PlayerId player);

    /// <summary>
    /// Notifies the room's game that <paramref name="player"/> has left (clean leave or
    /// disconnect), so it can release that player's state. Forwards to the game's <c>OnLeave</c>.
    /// </summary>
    void Leave(PlayerId player);

    /// <summary>
    /// Signals the room's game that the room is being torn down (reap/shutdown), so it can
    /// finalize. Called once as the room is removed. Forwards to the game's <c>OnTerminate</c>.
    /// </summary>
    void Terminate();

    bool HasPlayer(PlayerId player);

    /// <summary>
    /// Validates and queues a command for the next tick. Non-members, stale sequences,
    /// and commands the game rejects are all turned away here, before any state is touched.
    /// </summary>
    CommandAdmission TryEnqueue(PlayerId player, string command, long sequence);

    /// <summary>Advances one tick: applies all queued commands in order and produces a snapshot + events.</summary>
    TickResult Tick();

    /// <summary>Projects current authoritative state into the generic replication model.</summary>
    IReadOnlyList<EntitySnapshot> Project();

    /// <summary>The current authoritative snapshot (opaque game state) without advancing the clock.</summary>
    RoomSnapshot Snapshot();

    /// <summary>Resets the room to a stored snapshot (game state + tick). Recovery seam.</summary>
    void RestoreFrom(RoomSnapshot snapshot);

    /// <summary>
    /// Replays one already-accepted event during recovery, applying its command effect
    /// and advancing the room to the event's tick. Returns false if the game cannot
    /// apply the command (unrecognized/corrupt recovery data), so recovery fails
    /// explicitly rather than producing silently-wrong state.
    /// </summary>
    bool ApplyRecoveredEvent(RoomEvent recoveredEvent);
}
