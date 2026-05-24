using System.Diagnostics;
using GameServer.Simulation;
using GameServer.Transport;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// Gap #9 (connection scaling) — no idle/zombie reaping. A connection is admitted at accept time
/// and the loop then <c>await</c>s inbound with no deadline, so a client that connects and goes
/// silent (a half-open socket, a frozen device, a slowloris probe) parks a server task and its
/// buffers indefinitely. At 100k connections even a small fraction of these is a standing leak.
/// This scenario opens a connection that never sends anything and asserts the server closes it
/// within an idle deadline. It FAILS today (the loop never returns) and turns green once the loop
/// consults an <see cref="IIdleConnectionPolicy"/> and disconnects idle connections
/// (<c>DisconnectReason.IdleTimeout</c>) / heartbeats them.
/// </summary>
/// <remarks>Integration scenario: uses real wall-clock time, kept out of the deterministic fast loop.</remarks>
public sealed class IdleConnectionScenario
{
    private readonly ITestOutputHelper _output;

    public IdleConnectionScenario(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task SilentConnection_IsReapedWithinAnIdleDeadline()
    {
        // The deadline a hardened server should honor for a connection that never even says hello.
        var idleDeadline = TimeSpan.FromSeconds(1.5);

        var harness = new IntegrationHarness(_ => new MoveRightGame());
        var connection = harness.OpenConnectionFor("tenant-a", "p1");

        // It was admitted (counts against capacity) but is completely silent.
        Assert.Equal(1, harness.Server.ActiveConnectionCount);

        var sw = Stopwatch.StartNew();
        var winner = await Task.WhenAny(connection.Loop, Task.Delay(idleDeadline));
        sw.Stop();
        var reaped = winner == connection.Loop;

        // Drain so the test never hangs whether or not the server reaped it.
        await connection.CloseAsync();

        _output.WriteLine($"idle connection reaped by server: {reaped} (waited {sw.ElapsedMilliseconds}ms)");

        Assert.True(
            reaped,
            $"Gap #9: an idle connection was not closed by the server within {idleDeadline.TotalSeconds}s. " +
            "HandleConnectionAsync awaits ReceiveAsync with no idle deadline; it must consult an IIdleConnectionPolicy " +
            "to heartbeat and then disconnect silent/zombie connections (DisconnectReason.IdleTimeout) so they cannot accumulate.");
    }
}
