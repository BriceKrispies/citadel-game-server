using GameServer.Simulation;
using GameServer.Transport;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// Gap #10 (connection scaling) — dead subscribers linger after a clean disconnect. Under
/// <see cref="RoomLifecycle.Persist"/> a room is (intentionally) kept after its clients leave for
/// a fast rejoin — but <c>OnDisconnect</c> only prunes the leaving connection from the room's
/// subscriber set under <see cref="RoomLifecycle.Reap"/>. So a connection that closes cleanly
/// stays registered as a viewer: it keeps a replicator baseline and still receives fan-out work
/// every tick. Under churn this is a steady leak. This scenario joins a client, lets it close
/// cleanly, and asserts the room has no viewers left. It FAILS today (the closed connection
/// lingers) and turns green once a clean disconnect always prunes the subscriber, regardless of
/// lifecycle policy (the room may persist; the dead connection must not).
/// </summary>
public sealed class DisconnectSubscriberLeakScenario
{
    private readonly ITestOutputHelper _output;

    public DisconnectSubscriberLeakScenario(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task CleanlyClosedConnection_FreesItsRoomSlot_UnderPersist()
    {
        var harness = new IntegrationHarness(_ => new MoveRightGame(), lifecycle: RoomLifecycle.Persist);
        var key = harness.Key("tenant-a", "arena");

        // RunClientAsync performs hello + join, then closes the client and awaits the server loop,
        // so by the time it returns the connection has fully (and cleanly) disconnected.
        await harness.RunClientAsync("tenant-a", "arena", "p1", "demo", Array.Empty<string>());

        Assert.True(harness.Server.TryObserveRoom(key, out var observation));
        _output.WriteLine($"viewers still registered after clean disconnect: {observation.Viewers.Count}");

        Assert.Empty(observation.Viewers); // RED: the dead connection lingers as a viewer under Persist.
    }
}
