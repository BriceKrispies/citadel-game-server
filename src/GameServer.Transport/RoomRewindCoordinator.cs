using GameServer.Observability;
using GameServer.Protocol;
using GameServer.Routing;

namespace GameServer.Transport;

/// <summary>One room's result within a bulk rewind.</summary>
public sealed record RoomRewindEntry(RoomKey Key, long TargetTick, RoomRewindOutcome Outcome);

/// <summary>Aggregate result of a bulk rewind: every room's outcome plus quick tallies.</summary>
public sealed record RoomRewindReport(IReadOnlyList<RoomRewindEntry> Entries)
{
    public int Total => Entries.Count;
    public int Rewound => Entries.Count(e => e.Outcome == RoomRewindOutcome.Rewound);
    public int Skipped => Entries.Count(e => e.Outcome is RoomRewindOutcome.NotFound or RoomRewindOutcome.BeyondHorizon);
    public int Failed => Entries.Count(e => e.Outcome is RoomRewindOutcome.Failed or RoomRewindOutcome.NotRewindable);
}

/// <summary>
/// Orchestrates rewinding many rooms at once — per tenant or across the whole process. It holds the
/// <see cref="TickGate"/> for the duration so the authoritative tick driver is quiesced while room
/// state is swapped, then rewinds each room through the authoritative <see cref="RealtimeServer.RewindRoom"/>
/// pathway. One room's failure is isolated (recorded, never aborting the batch), mirroring the tick
/// driver's per-room resilience. Per-room enumeration stays within a tenant's own stores, so the
/// global variant cannot cross a tenant boundary.
/// </summary>
public sealed class RoomRewindCoordinator
{
    private readonly RealtimeServer _server;
    private readonly ISessionRouter _router;
    private readonly TickGate _gate;
    private readonly ITelemetrySink _telemetry;

    public RoomRewindCoordinator(RealtimeServer server, ISessionRouter router, TickGate gate, ITelemetrySink telemetry)
    {
        _server = server;
        _router = router;
        _gate = gate;
        _telemetry = telemetry;
    }

    /// <summary>Rewinds every placed room belonging to <paramref name="tenant"/>.</summary>
    public RoomRewindReport RewindTenant(TenantId tenant, IRewindTargetSelector selector, string reason) =>
        RewindMany(_router.RoomKeys.Where(k => k.TenantId == tenant), selector, reason);

    /// <summary>Rewinds every placed room in the process, across all tenants (platform-operator scope).</summary>
    public RoomRewindReport RewindAll(IRewindTargetSelector selector, string reason) =>
        RewindMany(_router.RoomKeys, selector, reason);

    /// <summary>
    /// Rewinds the given rooms while holding the tick gate, computing each room's target from the
    /// selector and its current tick. Returns a per-room report; never throws for an individual room.
    /// </summary>
    public RoomRewindReport RewindMany(IEnumerable<RoomKey> keys, IRewindTargetSelector selector, string reason)
    {
        var entries = new List<RoomRewindEntry>();

        // Quiesce ticking for the whole batch so no room is ticked mid-swap.
        using (_gate.Pause())
        {
            foreach (var key in keys)
            {
                long target = -1;
                RoomRewindOutcome outcome;
                try
                {
                    if (!_server.TryObserveRoom(key, out var observation))
                    {
                        // The room is not placed (e.g. reaped between enumeration and here).
                        outcome = RoomRewindOutcome.NotFound;
                    }
                    else
                    {
                        target = selector.TargetTickFor(key, observation.Tick);
                        outcome = _server.RewindRoom(key, target, reason);
                    }
                }
                catch (Exception ex)
                {
                    // Isolate a single room's failure: record it and keep going, never aborting the batch.
                    _telemetry.Event(TelemetryEvents.RoomRewound, new Dictionary<string, string>
                    {
                        ["tenantId"] = key.TenantId.Value,
                        ["roomId"] = key.RoomId.Value,
                        ["reason"] = reason,
                        ["error"] = ex.GetType().Name,
                    });
                    outcome = RoomRewindOutcome.Failed;
                }

                entries.Add(new RoomRewindEntry(key, target, outcome));
            }
        }

        return new RoomRewindReport(entries);
    }
}
