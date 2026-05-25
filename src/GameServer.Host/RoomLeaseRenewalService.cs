using GameServer.Routing;
using GameServer.Transport;

namespace GameServer.Host;

/// <summary>
/// Keeps this node's room-ownership leases alive. A distributed directory (Redis) leases ownership for
/// a bounded window; without renewal the lease lapses while a room is still being served and another
/// node could then claim it — the split brain the directory exists to prevent. On a cadence well inside
/// the lease, this re-claims every room the node is actively serving (<see cref="RealtimeServer.ActiveRooms"/>);
/// re-claiming for the current owner refreshes the lease, and re-claiming a room owned by another node
/// is a no-op (the fence rejects it). For the in-memory directory (no expiry) this is a cheap idempotent
/// no-op. Runs as a supervised worker, so a transient directory fault is restarted rather than fatal.
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
                    // Refreshes the lease for the current owner; a no-op if another node owns it.
                    _directory.TryClaim(room, _localNode);
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
