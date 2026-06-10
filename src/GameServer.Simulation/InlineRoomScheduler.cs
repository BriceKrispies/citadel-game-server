namespace GameServer.Simulation;

/// <summary>
/// The deterministic driver for room actors: it drains a <see cref="RoomActor"/>'s mailbox to idle
/// synchronously, on the calling thread, with no wall-clock time and no background threads. This is
/// the actor-model counterpart of the manually-driven <c>FakeSimulationClock</c> — a test or a
/// replay pumps the actor by hand, so the full receive→apply→reply path runs and is observable
/// without any timing.
/// </summary>
/// <remarks>
/// Production hosts will swap in a threaded driver (a loop awaiting a channel) at the transport edge;
/// it honors the same <see cref="RoomActor"/> contract, so business logic behaves identically under
/// either driver. Keeping the drain loop here — not on the actor — preserves the split between "the
/// actor knows how to apply one message" and "the driver decides the cadence."
/// </remarks>
public sealed class InlineRoomScheduler
{
    /// <summary>
    /// Applies every message currently in the actor's mailbox, in order, until it is empty. Returns
    /// the number of messages processed. Messages a handler posts back are NOT drained by this call
    /// (the actor wraps a room, which does not re-post), so this terminates deterministically.
    /// </summary>
    public int Drain(RoomActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var processed = 0;
        while (actor.TryProcessOne())
        {
            processed++;
        }

        return processed;
    }

    /// <summary>Drains each actor in turn to idle — the basis for driving a set of independent rooms.</summary>
    public int Drain(IEnumerable<RoomActor> actors)
    {
        ArgumentNullException.ThrowIfNull(actors);

        var processed = 0;
        foreach (var actor in actors)
        {
            processed += Drain(actor);
        }

        return processed;
    }
}
