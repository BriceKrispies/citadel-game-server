using GameServer.Observability;
using GameServer.Persistence;
using GameServer.Protocol;
using GameServer.Simulation;

namespace GameServer.Routing;

/// <summary>How a restore terminated. Always explicit and observable — never silent.</summary>
public enum RoomRecoveryOutcome
{
    /// <summary>State was rebuilt from a stored snapshot (plus any post-snapshot replay).</summary>
    RestoredFromSnapshot,

    /// <summary>No snapshot existed and policy permitted starting an empty room.</summary>
    StartedEmpty,

    /// <summary>Recovery data was missing/corrupt/unrecognized; no state was produced.</summary>
    Failed,
}

/// <summary>What to do when a room has no stored snapshot to restore from.</summary>
public enum MissingSnapshotPolicy
{
    /// <summary>Treat a missing snapshot as a recovery failure.</summary>
    Fail,

    /// <summary>Start a fresh, empty room at tick 0.</summary>
    StartEmpty,
}

/// <summary>
/// Outcome of a room recovery attempt. Exposes the restored room (when produced)
/// and enough metadata to verify and observe the restore.
/// </summary>
public sealed record RoomRecoveryResult(
    RoomRecoveryOutcome Outcome,
    IGameRoom? Room,
    long RestoredTick,
    int ReplayedEventCount);

/// <summary>
/// Rebuilds an authoritative room from its stored snapshot and post-snapshot event
/// log, for crash recovery. Reads only the keyed tenant/room data, replays only
/// events newer than the snapshot tick (so already-folded commands are not
/// duplicated), and emits restore telemetry.
/// </summary>
/// <remarks>
/// RED-phase seam: the contract exists so behavioral tests can be written, but the
/// recovery behavior is intentionally not implemented yet.
/// </remarks>
public sealed class RoomRecoveryService
{
    private readonly ISnapshotStore<RoomKey, RoomSnapshot> _snapshots;
    private readonly IEventLog<RoomKey, RoomEvent> _events;
    private readonly GameRoomFactory _roomFactory;
    private readonly ITelemetrySink _telemetry;

    public RoomRecoveryService(
        ISnapshotStore<RoomKey, RoomSnapshot> snapshots,
        IEventLog<RoomKey, RoomEvent> events,
        GameRoomFactory roomFactory,
        ITelemetrySink telemetry)
    {
        _snapshots = snapshots;
        _events = events;
        _roomFactory = roomFactory;
        _telemetry = telemetry;
    }

    /// <summary>
    /// Restores the room identified by <paramref name="key"/> for the given game.
    /// </summary>
    public RoomRecoveryResult Restore(
        RoomKey key,
        GameId gameId,
        MissingSnapshotPolicy missingSnapshotPolicy = MissingSnapshotPolicy.Fail)
    {
        var room = _roomFactory(key.RoomId, gameId);

        long baselineTick;
        RoomRecoveryOutcome outcome;

        if (_snapshots.TryGetLatest(key, out var snapshot))
        {
            room.RestoreFrom(snapshot);
            baselineTick = snapshot.Tick;
            outcome = RoomRecoveryOutcome.RestoredFromSnapshot;
        }
        else if (missingSnapshotPolicy == MissingSnapshotPolicy.StartEmpty)
        {
            // The freshly created room is already empty at tick 0.
            baselineTick = 0;
            outcome = RoomRecoveryOutcome.StartedEmpty;
        }
        else
        {
            return Complete(key, gameId, RoomRecoveryOutcome.Failed, room: null, restoredTick: 0, replayedEventCount: 0);
        }

        // Replay only events newer than the baseline tick so commands already folded
        // into the snapshot are never applied twice. Reads are strictly by RoomKey,
        // so a tenant can never observe another tenant's events.
        var replayedEventCount = 0;
        foreach (var recoveredEvent in _events.Read(key))
        {
            if (recoveredEvent.Tick <= baselineTick)
            {
                continue;
            }

            // Replay via the game; a command the game cannot accept is corrupt or
            // unrecognized recovery data — fail explicitly rather than produce
            // silently-wrong state.
            if (!room.ApplyRecoveredEvent(recoveredEvent))
            {
                return Complete(key, gameId, RoomRecoveryOutcome.Failed, room: null, baselineTick, replayedEventCount);
            }

            replayedEventCount++;
        }

        return Complete(key, gameId, outcome, room, room.Snapshot().Tick, replayedEventCount);
    }

    private RoomRecoveryResult Complete(
        RoomKey key,
        GameId gameId,
        RoomRecoveryOutcome outcome,
        IGameRoom? room,
        long restoredTick,
        int replayedEventCount)
    {
        var fields = new Dictionary<string, string>
        {
            ["tenantId"] = key.TenantId.Value,
            ["roomId"] = key.RoomId.Value,
            ["gameId"] = gameId.Value,
            ["restoredTick"] = restoredTick.ToString(),
            ["replayedEventCount"] = replayedEventCount.ToString(),
            ["outcome"] = outcome.ToString(),
        };

        _telemetry.Increment(TelemetryMetrics.RoomRestoreCount, fields);
        _telemetry.Event(TelemetryEvents.RoomRestored, fields);

        return new RoomRecoveryResult(outcome, room, restoredTick, replayedEventCount);
    }
}
