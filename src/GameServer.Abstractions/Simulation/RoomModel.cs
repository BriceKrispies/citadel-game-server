using GameServer.Protocol;

namespace GameServer.Simulation;

/// <summary>Outcome of attempting to admit a command into the room's queue.</summary>
public enum CommandAdmission
{
    Accepted,
    RejectedNotJoined,
    RejectedStaleSequence,

    /// <summary>The command is not a legal/known command for the room's game.</summary>
    RejectedInvalidCommand,

    /// <summary>
    /// The room's bounded command queue is full: the client is producing commands
    /// faster than ticks drain them. Shed explicitly (the client may retry the same
    /// sequence once the queue drains) rather than grow the queue without limit.
    /// </summary>
    RejectedOverloaded,
}

/// <summary>
/// Authoritative room state at a tick, as opaque game-serialized bytes. The platform
/// stores and restores these without interpreting them; only the game understands
/// <see cref="State"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Seed"/> and <see cref="GameSchemaVersion"/> form the cross-process REPLAY
/// HEADER. A snapshot alone restores state at <see cref="Tick"/>; to replay the events
/// recorded AFTER the snapshot to the identical entity state on a FRESH process, the room's
/// deterministic random source must be re-seeded with the exact seed the original room used —
/// otherwise any stochastic rule diverges and replay is non-deterministic. The schema version
/// records which game-state layout produced <see cref="State"/>, so a restore against an
/// incompatible game build can be detected rather than silently mis-deserialized.
/// </para>
/// <para>
/// Both are trailing, defaulted parameters so this stays BACK-COMPATIBLE: every existing
/// <c>new RoomSnapshot(tick, state)</c> caller compiles unchanged, and a previously-persisted
/// snapshot JSON (without these fields) deserializes with the defaults — a seed of 0 and an
/// unversioned schema, exactly the pre-header behavior.
/// </para>
/// </remarks>
public sealed record RoomSnapshot(long Tick, byte[] State, int Seed = 0, int GameSchemaVersion = 0);

/// <summary>
/// A durable fact for the event log / replay: a player's accepted command at a tick.
/// The command is the game-defined string; the game interprets it on replay.
/// </summary>
public sealed record RoomEvent(long Tick, PlayerId Player, string Command);

/// <summary>
/// Result of advancing the room by one tick: the new authoritative snapshot plus the
/// events produced this tick. The room returns these rather than writing to any store,
/// so the simulation stays free of persistence concerns.
/// </summary>
public sealed record TickResult(RoomSnapshot Snapshot, IReadOnlyList<RoomEvent> Events);
