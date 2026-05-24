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
}

/// <summary>
/// Authoritative room state at a tick, as opaque game-serialized bytes. The platform
/// stores and restores these without interpreting them; only the game understands
/// <see cref="State"/>.
/// </summary>
public sealed record RoomSnapshot(long Tick, byte[] State);

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
