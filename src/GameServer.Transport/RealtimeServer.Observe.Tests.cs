using GameServer.Protocol;
using GameServer.Simulation;
using GameServer.Transport.Testing;
using Xunit;

namespace GameServer.Transport;

/// <summary>
/// Tests the read-only admin observation: it reports a room's authoritative tick, its
/// projected entities (with the generic relevance key), and the connected viewers —
/// without mutating anything — and refuses an unknown room.
/// </summary>
public sealed class RealtimeServerObserveTests
{
    private static readonly RoomId Arena = new("arena");

    [Fact]
    public async Task TryObserveRoom_ReportsTickEntitiesAndViewers()
    {
        var harness = new SliceHarness("tenant-a");
        var (transport, client) = harness.NewClient("c1", "tenant-a", "p1");
        client.Hello();
        client.Join(Arena);
        client.Command(MoveRightGame.MoveRight, Arena);
        client.Close();
        await harness.Server.HandleConnectionAsync(transport, client.Principal);

        var key = harness.Key("tenant-a", "arena");
        await harness.Server.TickRoom(key);

        Assert.True(harness.Server.TryObserveRoom(key, out var observation));
        Assert.Equal("tenant-a", observation.TenantId);
        Assert.Equal("arena", observation.RoomId);
        Assert.Equal(1, observation.Tick);

        var entity = Assert.Single(observation.Entities);
        Assert.Equal("p1", entity.EntityId);
        Assert.Equal(1, entity.X); // moved right once → relevance key X = 1, no payload decode needed

        var viewer = Assert.Single(observation.Viewers);
        Assert.Equal("c1", viewer.ConnectionId);
        Assert.Equal("p1", viewer.PlayerId);
    }

    [Fact]
    public void TryObserveRoom_UnknownRoom_ReturnsFalse()
    {
        var harness = new SliceHarness("tenant-a");

        Assert.False(harness.Server.TryObserveRoom(harness.Key("tenant-a", "nope"), out _));
    }

    [Fact]
    public async Task TryObserveRoom_DoesNotMutate_TickStaysWhereItWas()
    {
        var harness = new SliceHarness("tenant-a");
        var (transport, client) = harness.NewClient("c1", "tenant-a", "p1");
        client.Hello();
        client.Join(Arena);
        client.Close();
        await harness.Server.HandleConnectionAsync(transport, client.Principal);

        var key = harness.Key("tenant-a", "arena");
        await harness.Server.TickRoom(key);

        Assert.True(harness.Server.TryObserveRoom(key, out var first));
        Assert.True(harness.Server.TryObserveRoom(key, out var second));
        // Observing twice does not advance the room.
        Assert.Equal(first.Tick, second.Tick);
    }
}
