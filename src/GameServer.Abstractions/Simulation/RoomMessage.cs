using GameServer.Protocol;
using GameServer.Replication;

namespace GameServer.Simulation;

/// <summary>
/// One unit of work for a room actor. Every interaction with a room's authoritative
/// business logic — admitting a command, advancing a tick, a player joining or leaving,
/// taking a snapshot, replaying a recovered event — is one of these messages, delivered to
/// the room's mailbox and applied by the room's single consumer in arrival order.
/// </summary>
/// <remarks>
/// This is the in-process analogue of the out-of-process room RPC protocol: the room is the
/// only thing that touches its state, so serializing the mailbox is what lets external callers
/// drop every <c>lock(room)</c>. The union is a pure data contract (a port DTO): it describes
/// the operation; the rank-1 room actor owns the dispatch that maps each message onto the
/// <see cref="IGameRoom"/> and completes the reply. Messages split into two families:
/// fire-and-forget <em>tells</em> (no reply) and request/reply <em>asks</em>
/// (<see cref="RoomAsk{TReply}"/>, whose <see cref="RoomAsk{TReply}.Reply"/> the caller awaits).
/// </remarks>
public abstract record RoomMessage;

/// <summary>
/// The non-generic facet of an ask, so a dispatcher can fault any pending reply without knowing
/// its reply type (e.g. when a handler throws, the awaiting caller observes the exception rather
/// than hanging forever).
/// </summary>
public interface IRoomAsk
{
    /// <summary>Faults the pending reply so an awaiting caller observes the error instead of hanging.</summary>
    void Fail(Exception error);
}

/// <summary>
/// A <see cref="RoomMessage"/> whose handling produces a reply of type <typeparamref name="TReply"/>.
/// The reply handle is a <see cref="TaskCompletionSource{TResult}"/> the dispatcher completes once it
/// has applied the message; the caller awaits <see cref="Reply"/>. Continuations run asynchronously so
/// completing a reply from inside the mailbox loop can never re-enter the loop on the completing thread.
/// </summary>
public abstract record RoomAsk<TReply> : RoomMessage, IRoomAsk
{
    private readonly TaskCompletionSource<TReply> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The reply the caller awaits; completes when the dispatcher applies this message.</summary>
    public Task<TReply> Reply => _completion.Task;

    /// <summary>Completes the reply with the handler's result. Idempotent (first completion wins).</summary>
    public void Complete(TReply value) => _completion.TrySetResult(value);

    /// <summary>Faults the reply when the handler throws, so the awaiting caller observes the error.</summary>
    public void Fail(Exception error) => _completion.TrySetException(error);
}

// --- Tells (fire-and-forget): membership and lifecycle signals with no reply ---

/// <summary>Admits a player to the room's game (the platform has already authorized them).</summary>
public sealed record JoinPlayer(PlayerId Player) : RoomMessage;

/// <summary>Signals the room's game that a player has left (clean leave or disconnect).</summary>
public sealed record LeavePlayer(PlayerId Player) : RoomMessage;

/// <summary>Signals the room's game that the room is being torn down, so it can finalize.</summary>
public sealed record TerminateRoom : RoomMessage;

/// <summary>Resets the room to a stored snapshot (game state + tick). The recovery seam.</summary>
public sealed record RestoreRoom(RoomSnapshot Snapshot) : RoomMessage;

// --- Asks (request/reply): the caller awaits the result of the operation ---

/// <summary>Validates and queues a command for the next tick; the reply is the admission outcome.</summary>
public sealed record EnqueueCommand(PlayerId Player, string Command, long Sequence) : RoomAsk<CommandAdmission>;

/// <summary>Advances one tick: applies queued commands in order; the reply is the snapshot + events.</summary>
public sealed record AdvanceTick : RoomAsk<TickResult>;

/// <summary>Asks whether the room's game will admit a player right now.</summary>
public sealed record QueryCanJoin(PlayerId Player) : RoomAsk<bool>;

/// <summary>Asks whether a player is currently a member of the room's game.</summary>
public sealed record QueryHasPlayer(PlayerId Player) : RoomAsk<bool>;

/// <summary>Asks the current command-queue depth — the room's backpressure gauge.</summary>
public sealed record QueryQueueDepth : RoomAsk<int>;

/// <summary>Asks for the current authoritative snapshot without advancing the clock.</summary>
public sealed record CaptureSnapshot : RoomAsk<RoomSnapshot>;

/// <summary>Asks for a projection of current authoritative state into the generic replication model.</summary>
public sealed record ProjectEntities : RoomAsk<IReadOnlyList<EntitySnapshot>>;

/// <summary>Replays one already-accepted event during recovery; the reply is whether it applied.</summary>
public sealed record ReplayRecoveredEvent(RoomEvent Event) : RoomAsk<bool>;
