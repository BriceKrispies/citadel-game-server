using GameServer.Routing;

namespace GameServer.Transport;

/// <summary>
/// Chooses the tick to rewind a room to, given the room's current tick. A bulk rewind cannot use one
/// shared tick number across rooms — rooms advance independently, so "the same tick" is meaningless —
/// so the target is computed per room from a policy (rewind to an absolute tick, or back by N ticks).
/// </summary>
public interface IRewindTargetSelector
{
    /// <summary>The tick to rewind <paramref name="key"/> to, given its <paramref name="currentTick"/>.</summary>
    long TargetTickFor(RoomKey key, long currentTick);
}

/// <summary>Rewinds every room to the same absolute tick (clamped to its current tick — never forward).</summary>
public sealed class RewindToTick : IRewindTargetSelector
{
    private readonly long _tick;

    public RewindToTick(long tick) => _tick = tick < 0 ? 0 : tick;

    public long TargetTickFor(RoomKey key, long currentTick) => Math.Min(_tick, currentTick);
}

/// <summary>Rewinds every room back by a fixed number of ticks from its own current tick (floored at 0).</summary>
public sealed class RewindByTicks : IRewindTargetSelector
{
    private readonly long _ticks;

    public RewindByTicks(long ticks) => _ticks = ticks < 0 ? 0 : ticks;

    public long TargetTickFor(RoomKey key, long currentTick) => Math.Max(0, currentTick - _ticks);
}
