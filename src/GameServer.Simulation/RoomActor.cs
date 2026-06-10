namespace GameServer.Simulation;

/// <summary>
/// Wraps an <see cref="IGameRoom"/> as a single-consumer actor. All work arrives as
/// <see cref="RoomMessage"/>s on a mailbox and is applied one at a time in arrival order, so the
/// wrapped room's authoritative business logic is touched by exactly one logical thread — the
/// mailbox is the serialization boundary that replaces every external <c>lock(room)</c>.
/// </summary>
/// <remarks>
/// The actor adds NO game behavior: it only sequences delivery and routes each message to the
/// matching <see cref="IGameRoom"/> call, completing an ask's reply with the result. The room
/// itself is unchanged and stays deterministic. Draining the mailbox is a driver's job (see
/// <see cref="InlineRoomScheduler"/> for the deterministic pump); this type holds the mailbox and
/// knows how to apply one message, but never decides <em>when</em> to run — so the same actor is
/// driven inline in tests and by a threaded loop in production without changing its behavior.
/// </remarks>
public sealed class RoomActor : IRoomActor
{
    private readonly IGameRoom _room;
    private readonly Queue<RoomMessage> _mailbox = new();

    public RoomActor(IGameRoom room) => _room = room;

    public RoomId Id => _room.Id;

    /// <summary>Messages currently waiting in the mailbox — the actor-level backpressure gauge.</summary>
    public int PendingCount => _mailbox.Count;

    public void Post(RoomMessage message) => _mailbox.Enqueue(message);

    /// <summary>
    /// Applies the next queued message to the wrapped room and completes its reply if it is an ask.
    /// Returns <c>false</c> when the mailbox is empty. A handler that throws while serving an ask
    /// faults that ask's reply (the awaiting caller observes the error) and the actor survives; a
    /// throwing tell has no reply to carry the fault, so it propagates to the driver — matching the
    /// scheduler convention that an unexpected fault aborts the cycle rather than being swallowed.
    /// </summary>
    public bool TryProcessOne()
    {
        if (!_mailbox.TryDequeue(out var message))
        {
            return false;
        }

        try
        {
            Dispatch(message);
        }
        catch (Exception ex) when (message is IRoomAsk ask)
        {
            ask.Fail(ex);
        }

        return true;
    }

    private void Dispatch(RoomMessage message)
    {
        switch (message)
        {
            case JoinPlayer m:
                _room.Join(m.Player);
                break;
            case LeavePlayer m:
                _room.Leave(m.Player);
                break;
            case TerminateRoom:
                _room.Terminate();
                break;
            case RestoreRoom m:
                _room.RestoreFrom(m.Snapshot);
                break;
            case EnqueueCommand m:
                m.Complete(_room.TryEnqueue(m.Player, m.Command, m.Sequence));
                break;
            case AdvanceTick m:
                m.Complete(_room.Tick());
                break;
            case QueryCanJoin m:
                m.Complete(_room.CanJoin(m.Player));
                break;
            case QueryHasPlayer m:
                m.Complete(_room.HasPlayer(m.Player));
                break;
            case QueryQueueDepth m:
                m.Complete(_room.QueueDepth);
                break;
            case CaptureSnapshot m:
                m.Complete(_room.Snapshot());
                break;
            case ProjectEntities m:
                m.Complete(_room.Project());
                break;
            case ReplayRecoveredEvent m:
                m.Complete(_room.ApplyRecoveredEvent(m.Event));
                break;
            default:
                // A new message type was added without a handler: fail loudly rather than drop the
                // work silently. An ask turns this into a faulted reply (see TryProcessOne); a tell
                // propagates it to the driver.
                throw new ArgumentOutOfRangeException(
                    nameof(message), message.GetType().Name, "No room actor handler for this message type.");
        }
    }
}
