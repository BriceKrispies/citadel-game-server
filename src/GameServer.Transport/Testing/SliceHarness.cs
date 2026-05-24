using GameServer.Observability.Testing;
using GameServer.Persistence;
using GameServer.Protocol;
using GameServer.Routing;
using GameServer.Simulation;
using GameServer.Simulation.Testing;
using GameServer.Tenancy;

namespace GameServer.Transport.Testing;

/// <summary>
/// Wires the full in-process vertical slice from deterministic fakes: tenant
/// resolver, session router with a deterministic room factory, in-memory stores,
/// a recording telemetry sink, and the <see cref="RealtimeServer"/>. One place for
/// tests to compose the kernel without repeating plumbing.
/// </summary>
public sealed class SliceHarness
{
    public const string DefaultGame = "demo";

    public SliceHarness(params string[] tenantIds)
        : this(GameRoom.DefaultMaxQueueDepth, tenantIds)
    {
    }

    /// <summary>Overload for backpressure tests that need a small, easily-saturated command queue.</summary>
    public SliceHarness(int maxQueueDepth, params string[] tenantIds)
    {
        var tenants = tenantIds.Length == 0 ? new[] { "tenant-a" } : tenantIds;
        Tenants = new InMemoryTenantResolver(
            tenants.Select(t => new TenantContext(new TenantId(t), $"Tenant {t}")));

        // Each room gets the game its catalog entry selects, plus its own deterministic
        // clock + seeded random source, and the configured command-queue bound.
        Router = new InMemorySessionRouter(
            (roomId, gameId) => new GameRoom(roomId, GameFor(gameId), new FakeSimulationClock(), new DeterministicRandomSource(), maxQueueDepth));

        Snapshots = new InMemorySnapshotStore<RoomKey, RoomSnapshot>();
        Events = new InMemoryEventLog<RoomKey, RoomEvent>();
        Telemetry = new TestTelemetrySink();
        Server = new RealtimeServer(Tenants, Router, Snapshots, Events, Telemetry);
    }

    public InMemoryTenantResolver Tenants { get; }
    public InMemorySessionRouter Router { get; }
    public InMemorySnapshotStore<RoomKey, RoomSnapshot> Snapshots { get; }
    public InMemoryEventLog<RoomKey, RoomEvent> Events { get; }
    public TestTelemetrySink Telemetry { get; }
    public RealtimeServer Server { get; }

    public (InMemoryBidirectionalTransport Transport, FakeClient Client) NewClient(
        string connectionId, string tenantId, string playerId, string game = DefaultGame, string roomId = "arena")
    {
        var transport = new InMemoryBidirectionalTransport(new ConnectionId(connectionId));
        var client = new FakeClient(
            transport, new TenantId(tenantId), new GameId(game), new PlayerId(playerId), new RoomId(roomId));
        return (transport, client);
    }

    public RoomKey Key(string tenantId, string roomId) => new(new TenantId(tenantId), new RoomId(roomId));

    /// <summary>Selects the game for a room by its game id (mirrors the catalog).</summary>
    private static IGameSimulation GameFor(GameId gameId) => gameId.Value switch
    {
        "grid-walk" => new GridWalkGame(),
        _ => new MoveRightGame(),
    };
}
