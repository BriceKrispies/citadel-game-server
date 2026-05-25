using GameServer.Routing;
using GameServer.Transport;

namespace GameServer.Host;

/// <summary>
/// Keeps this node's room-ownership leases alive. A distributed directory (Redis) leases ownership for
/// a bounded window; without renewal the lease lapses while a room is still being served and another
/// node could then claim it — the split brain the directory exists to prevent. On a cadence well inside
/// the lease, this RENEWS (not re-claims) every room the node is actively serving
/// (<see cref="RealtimeServer.ActiveRooms"/>) via <see cref="IRoomDirectory.TryRenew"/>: it refreshes the
/// lease only while this node is still the owner, and acquires NOTHING when the room has been taken over.
/// Using renew rather than claim is deliberate — if this node was partitioned and lost a room, its renewal
/// must not resurrect ownership the instant the new owner's lease has a gap (which would co-own the room).
/// For the in-memory directory (no expiry) this is a cheap idempotent no-op. Runs as a supervised worker,
/// so a transient directory fault is restarted rather than fatal.
/// </summary>
public sealed class RoomLeaseRenewalService : ISupervisedWorker
{
    private readonly RealtimeServer _server;
    private readonly IRoomDirectory _directory;
    private readonly NodeId _localNode;
    private readonly TimeSpan _interval;
    private readonly ILogger<RoomLeaseRenewalService> _logger;

    public RoomLeaseRenewalService(
        RealtimeServer server,
        IRoomDirectory directory,
        NodeId localNode,
        TimeSpan interval,
        ILogger<RoomLeaseRenewalService> logger)
    {
        _server = server;
        _directory = directory;
        _localNode = localNode;
        _interval = interval;
        _logger = logger;
    }

    public string Name => "room-lease-renewal";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_interval);

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var room in _server.ActiveRooms)
            {
                try
                {
                    // RENEW (not claim): refreshes the lease only while this node is still the owner. If the
                    // node was partitioned and another node took the room over, this returns false and does
                    // NOT re-acquire it — re-acquisition would resurrect ownership and split-brain the room.
                    _directory.TryRenew(room, _localNode);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // A transient directory failure must not kill the loop — the next tick retries.
                    _logger.LogWarning(ex, "Lease renewal failed for room {Room}", room);
                }
            }
        }
    }
}
