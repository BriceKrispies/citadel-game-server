using GameServer.Replication;
using GameServer.Simulation;

namespace GameServer.Routing;

/// <summary>
/// A read-only walk through a room's recorded history, one tick at a time. Opened at a checkpoint
/// (via <see cref="RoomReplayService.OpenSession"/>) and loaded with the events that followed it, it
/// lets a caller advance the room tick by tick and project the authoritative state at each step — the
/// debugging/inspection face of the replay engine. The wrapped room is a sandbox: stepping never
/// touches the live system.
/// </summary>
public sealed class ReplaySession
{
    private readonly IGameRoom _room;
    private readonly IReadOnlyList<RoomEvent> _events;
    private int _cursor;

    internal ReplaySession(IGameRoom room, IReadOnlyList<RoomEvent> events)
    {
        _room = room;
        _events = events;
        CurrentTick = room.Snapshot().Tick;
    }

    /// <summary>The tick the session currently sits at (the checkpoint tick before any <see cref="Step"/>).</summary>
    public long CurrentTick { get; private set; }

    /// <summary>
    /// Advances to the next recorded tick, applying every event recorded at that tick in order. Returns
    /// false when there are no more recorded events (the session has reached the end of loaded history).
    /// Throws if a recorded event cannot be applied — corrupt history surfaces explicitly, never as
    /// silently-wrong state.
    /// </summary>
    public bool Step()
    {
        if (_cursor >= _events.Count)
        {
            return false;
        }

        var tick = _events[_cursor].Tick;
        while (_cursor < _events.Count && _events[_cursor].Tick == tick)
        {
            if (!_room.ApplyRecoveredEvent(_events[_cursor]))
            {
                throw new InvalidOperationException(
                    $"Replay history is corrupt: event at tick {tick} could not be applied.");
            }

            _cursor++;
        }

        CurrentTick = tick;
        return true;
    }

    /// <summary>The authoritative entity state as of <see cref="CurrentTick"/>, exactly as a client sees it.</summary>
    public IReadOnlyList<EntitySnapshot> Project() => _room.Project();
}
