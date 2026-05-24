using GameServer.Simulation;
using GameServer.Transport;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// Gap #10 (connection scaling) — connections must not leak room slots under churn. Under the
/// host's <see cref="RoomLifecycle.Reap"/> lifecycle, when the last connection in a room
/// disconnects the room is torn down: its subscriber set, replicator baselines, per-connection
/// outbound buffer, and routed room are all released, so memory tracks LIVE rooms rather than
/// every room ever joined. This scenario joins a client, lets it cleanly disconnect, and asserts
/// the room slot is freed (the room is no longer active or observable). It guards against the
/// churn leak where dead connections accumulate and keep consuming fan-out and memory.
/// </summary>
/// <remarks>
/// Under <see cref="RoomLifecycle.Persist"/> a closed connection is intentionally kept as a
/// subscriber so an operator can still observe a lagging/idle session (see
/// <c>AdminObservationScenario</c>); slot-freeing on disconnect is the <see cref="RoomLifecycle.Reap"/>
/// contract, which is what the production host runs.
/// </remarks>
public sealed class DisconnectSubscriberLeakScenario
{
    private readonly ITestOutputHelper _output;

    public DisconnectSubscriberLeakScenario(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task CleanlyClosedConnection_FreesItsRoomSlot_UnderReap()
    {
        var harness = new IntegrationHarness(_ => new MoveRightGame(), lifecycle: RoomLifecycle.Reap);
        var key = harness.Key("tenant-a", "arena");

        // RunClientAsync performs hello + join, then closes the client and awaits the server loop,
        // so by the time it returns the connection has fully (and cleanly) disconnected.
        await harness.RunClientAsync("tenant-a", "arena", "p1", "demo", Array.Empty<string>());

        _output.WriteLine($"active rooms after the last clean disconnect: {harness.Server.ActiveRooms.Count}");

        // The last leaver freed the slot: the room is neither active (tickable) nor observable.
        Assert.DoesNotContain(key, harness.Server.ActiveRooms);
        Assert.False(harness.Server.TryObserveRoom(key, out _));
    }
}
