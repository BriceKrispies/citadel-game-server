using GameServer.Observability;
using GameServer.Persistence;
using GameServer.Protocol;
using GameServer.Simulation;

namespace GameServer.Routing;

/// <summary>How a replay terminated. Always explicit and observable.</summary>
public enum RoomReplayOutcome
{
    /// <summary>State was reconstructed as of the requested target tick.</summary>
    Replayed,

    /// <summary>
    /// No checkpoint exists at or before the target tick — it predates the retained rewind horizon
    /// (or the room has no history). The point is no longer reachable; no state was produced.
    /// </summary>
    BeyondHorizon,

    /// <summary>An event could not be replayed (corrupt/unrecognized); no state was produced.</summary>
    Failed,
}

/// <summary>
/// Outcome of a replay. Exposes the reconstructed room (when produced) and enough metadata to
/// verify and observe it. The room is a SANDBOX instance — it is never registered in the router,
/// so replay is read-only with respect to the live system.
/// </summary>
public sealed record RoomReplayResult(
    RoomReplayOutcome Outcome,
    IGameRoom? Room,
    long ReplayedToTick,
    int ReplayedEventCount);

/// <summary>
/// Reconstructs a room as of any past tick within the retained history — the deterministic replay
/// engine. It restores the newest checkpoint at or before the target tick (its RNG state restores the
/// EXACT draw position, so even a stochastic game replays exactly from a mid-history checkpoint), then
/// replays the recorded events in <c>(checkpointTick, targetTick]</c>. Reads are strictly by
/// <see cref="RoomKey"/>, so a tenant can never observe another tenant's history.
/// </summary>
/// <remarks>
/// This generalizes crash recovery (<see cref="RoomRecoveryService"/> restores to the latest state);
/// replay targets an arbitrary tick and is the shared core beneath read-only inspection
/// (<see cref="ReplaySession"/>) and live rewind. Determinism note: replay reproduces authoritative
/// state exactly (restored RNG state + logical clock + recorded commands). It does not reproduce
/// wall-clock or external-IO effects — the kernel has none by design, so replay is faithful.
/// </remarks>
public sealed class RoomReplayService
{
    private readonly ISnapshotHistoryStore<RoomKey, RoomSnapshot> _history;
    private readonly IRewindableEventLog<RoomKey, RoomEvent> _events;
    private readonly GameRoomFactory _roomFactory;
    private readonly ITelemetrySink _telemetry;

    public RoomReplayService(
        ISnapshotHistoryStore<RoomKey, RoomSnapshot> history,
        IRewindableEventLog<RoomKey, RoomEvent> events,
        GameRoomFactory roomFactory,
        ITelemetrySink telemetry)
    {
        _history = history;
        _events = events;
        _roomFactory = roomFactory;
        _telemetry = telemetry;
    }

    /// <summary>
    /// Reconstructs the room identified by <paramref name="key"/> as of <paramref name="targetTick"/>.
    /// </summary>
    public RoomReplayResult ReplayTo(RoomKey key, GameId gameId, long targetTick)
    {
        var room = _roomFactory(key.RoomId, gameId);

        if (!_history.TryGetLatestAtOrBefore(key, targetTick, out var baseSnapshot))
        {
            return Complete(key, gameId, RoomReplayOutcome.BeyondHorizon, room: null, targetTick, replayedEventCount: 0);
        }

        room.RestoreFrom(baseSnapshot);

        // Replay only events newer than the checkpoint, up to and including the target tick. The
        // half-open low bound excludes events already folded into the checkpoint, so nothing is
        // applied twice; the inclusive high bound stops exactly at the requested point.
        var replayedEventCount = 0;
        foreach (var recoveredEvent in _events.ReadRange(key, fromExclusive: baseSnapshot.Tick, toInclusive: targetTick))
        {
            if (!room.ApplyRecoveredEvent(recoveredEvent))
            {
                return Complete(key, gameId, RoomReplayOutcome.Failed, room: null, targetTick, replayedEventCount);
            }

            replayedEventCount++;
        }

        // Position the clock exactly at the target tick. After replay the room's clock sits at the last
        // replayed event's tick, which is < targetTick when the trailing ticks carried no commands (the
        // game mutates only on applied commands, so those ticks changed nothing). Re-restoring the current
        // state with the tick adjusted reuses the restore primitive and preserves the RNG draw position
        // (the captured state is the live source's current position), so a resumed timeline advances from
        // targetTick + 1 cleanly.
        if (room.Snapshot().Tick != targetTick)
        {
            room.RestoreFrom(room.Snapshot() with { Tick = targetTick });
        }

        return Complete(key, gameId, RoomReplayOutcome.Replayed, room, targetTick, replayedEventCount);
    }

    /// <summary>
    /// Opens a step-through session positioned at the checkpoint at or before <paramref name="throughTick"/>,
    /// loaded with the recorded events up to it, so a caller can walk the room one recorded tick at a time
    /// and inspect state at each step. Returns null when the target predates retained history.
    /// </summary>
    public ReplaySession? OpenSession(RoomKey key, GameId gameId, long throughTick)
    {
        var room = _roomFactory(key.RoomId, gameId);
        if (!_history.TryGetLatestAtOrBefore(key, throughTick, out var baseSnapshot))
        {
            return null;
        }

        room.RestoreFrom(baseSnapshot);
        var events = _events.ReadRange(key, fromExclusive: baseSnapshot.Tick, toInclusive: throughTick);
        return new ReplaySession(room, events);
    }

    private RoomReplayResult Complete(
        RoomKey key,
        GameId gameId,
        RoomReplayOutcome outcome,
        IGameRoom? room,
        long replayedToTick,
        int replayedEventCount)
    {
        var fields = new Dictionary<string, string>
        {
            ["tenantId"] = key.TenantId.Value,
            ["roomId"] = key.RoomId.Value,
            ["gameId"] = gameId.Value,
            ["replayedToTick"] = replayedToTick.ToString(),
            ["replayedEventCount"] = replayedEventCount.ToString(),
            ["outcome"] = outcome.ToString(),
        };

        _telemetry.Increment(TelemetryMetrics.RoomReplayCount, fields);
        _telemetry.Event(TelemetryEvents.RoomReplayed, fields);

        return new RoomReplayResult(outcome, room, replayedToTick, replayedEventCount);
    }
}
