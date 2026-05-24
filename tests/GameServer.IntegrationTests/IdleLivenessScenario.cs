using System.Collections.Concurrent;
using System.Diagnostics;
using GameServer.Identity;
using GameServer.Protocol;
using GameServer.Simulation;
using GameServer.Transport;
using Xunit;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// Liveness defect (companion to <see cref="IdleConnectionScenario"/>): the realtime transport
/// answers pings and drops some frames at the edge, so a client sending only those produces no
/// inbound messages for the server loop — and was therefore reaped at the idle deadline despite
/// being demonstrably alive (this is exactly what wedged the load harness, whose input frames the
/// platform discards). The fix has the server treat advancing transport wire-activity
/// (<see cref="IInboundActivityProbe"/>) as proof of life: a busy connection is kept alive, while a
/// genuinely silent one is still reaped.
/// </summary>
/// <remarks>Integration scenario: uses real wall-clock time, kept out of the deterministic fast loop.</remarks>
public sealed class IdleLivenessScenario
{
    private readonly ITestOutputHelper _output;

    public IdleLivenessScenario(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task EstablishedConnection_WithEdgeHandledTraffic_IsNotReaped_ButGoesAwayWhenSilent()
    {
        var idleTimeout = TimeSpan.FromMilliseconds(500);
        var harness = new IntegrationHarness(_ => new MoveRightGame());
        var server = new GameServer.Transport.RealtimeServer(
            harness.Tenants, harness.Router, harness.Snapshots, harness.Events, harness.Telemetry,
            handshakeTimeout: idleTimeout,
            idlePolicy: new HeartbeatIdlePolicy(TimeSpan.FromMilliseconds(200), idleTimeout));

        const string tenant = "tenant-a", game = "demo", room = "arena", player = "p1";
        var claims = new JoinTokenClaims(tenant, game, room, player, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1));
        var transport = new ScriptedThenParkingTransport(new ConnectionId("c1"), Hello(tenant, game, player));

        var loop = server.HandleConnectionAsync(transport, claims);

        // Drive edge-handled traffic (frames the transport consumes without forwarding) across
        // several idle windows. The connection is established (hello forwarded) and active.
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromMilliseconds(1800))
        {
            transport.PulseInbound();
            await Task.Delay(150);
        }

        Assert.False(loop.IsCompleted,
            "an established connection sending wire frames the transport handles at the edge must not be reaped as idle.");

        // Now go silent: with no further wire activity, the idle deadline must reap it.
        var reaped = await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(3))) == loop;
        transport.Release(); // unpark for clean teardown

        _output.WriteLine($"kept alive while active, reaped after silence: {reaped} (frames seen: {transport.InboundFrameCount})");
        Assert.True(reaped, "a connection that goes silent past the idle deadline must still be reaped.");

        await loop;
    }

    private static MessageEnvelope Hello(string tenant, string game, string player) => new()
    {
        TenantId = new TenantId(tenant),
        GameId = new GameId(game),
        RoomId = null,
        PlayerId = new PlayerId(player),
        ProtocolVersion = ProtocolVersions.Current,
        MessageType = MessageType.ClientHello,
        Sequence = 0,
        TraceId = $"hello-{player}",
        Payload = new ClientHello(player),
    };

    /// <summary>
    /// Test transport that delivers a scripted message (a hello, to establish the session) and
    /// then parks — modelling a real adapter that has consumed and answered edge frames internally
    /// and is blocked awaiting the next forwardable frame. <see cref="PulseInbound"/> simulates the
    /// adapter reading another such edge frame off the wire (no kernel message produced).
    /// </summary>
    private sealed class ScriptedThenParkingTransport : IBidirectionalTransport, IInboundActivityProbe
    {
        private readonly ConcurrentQueue<MessageEnvelope> _scripted;
        private readonly TaskCompletionSource<MessageEnvelope?> _park = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _frames;

        public ScriptedThenParkingTransport(ConnectionId connectionId, params MessageEnvelope[] scripted)
        {
            ConnectionId = connectionId;
            _scripted = new ConcurrentQueue<MessageEnvelope>(scripted);
        }

        public ConnectionId ConnectionId { get; }

        public long InboundFrameCount => Interlocked.Read(ref _frames);

        public void PulseInbound() => Interlocked.Increment(ref _frames);

        public void Release() => _park.TrySetResult(null);

        public Task SendAsync(MessageEnvelope message, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<MessageEnvelope?> ReceiveAsync(CancellationToken cancellationToken = default) =>
            _scripted.TryDequeue(out var message) ? Task.FromResult<MessageEnvelope?>(message) : _park.Task;
    }
}
