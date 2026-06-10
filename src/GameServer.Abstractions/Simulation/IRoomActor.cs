using GameServer.Protocol;

namespace GameServer.Simulation;

/// <summary>
/// A room as an actor: the single owner of one room's authoritative business logic, addressed
/// only by posting <see cref="RoomMessage"/>s to its mailbox. The actor applies messages one at
/// a time in arrival order, so it — not an external lock — is the serialization boundary for all
/// room state. Callers never reach in and mutate; they post a tell or post an ask and await its
/// <see cref="RoomAsk{TReply}.Reply"/>.
/// </summary>
/// <remarks>
/// This is the port. How the mailbox is drained is a separable driver concern: a deterministic
/// inline pump for tests/replay, a threaded loop in production — both honor this same contract,
/// so business logic never knows which is driving it. The actor wraps an <see cref="IGameRoom"/>,
/// so the in-process and out-of-process rooms are addressed identically.
/// </remarks>
public interface IRoomActor
{
    /// <summary>The room this actor owns.</summary>
    RoomId Id { get; }

    /// <summary>
    /// Delivers a message to the room's mailbox and returns immediately. For an ask, await the
    /// message's <see cref="RoomAsk{TReply}.Reply"/> for the result; a tell has no reply.
    /// </summary>
    void Post(RoomMessage message);
}
