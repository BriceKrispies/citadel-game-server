using System.Diagnostics;
using System.Threading.Channels;
using GameServer.Identity;
using GameServer.Protocol;
using GameServer.Simulation;
using GameServer.Transport;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// Gap #8 (connection scaling) — head-of-line blocking in fan-out. <see cref="RealtimeServer.TickRoom"/>
/// fans a snapshot out to a room's subscribers by <c>await</c>ing each connection's
/// <see cref="IServerPushTransport.SendAsync"/> inline. A single slow/stalled client therefore
/// freezes the entire room's authoritative tick — and, at scale, every client in that room. This
/// scenario subscribes one client whose socket writes block indefinitely, then asserts the tick
/// still returns promptly (it must not block on client I/O). It FAILS today and turns green once
/// fan-out enqueues to a bounded per-connection <see cref="IOutboundChannel"/> instead of awaiting
/// the socket.
/// </summary>
/// <remarks>Integration scenario: uses real wall-clock time, kept out of the deterministic fast loop.</remarks>
public sealed class SlowClientFanoutScenario
{
    private readonly ITestOutputHelper _output;

    public SlowClientFanoutScenario(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task SlowClient_DoesNotStallTheRoomTick()
    {
        const string tenant = "tenant-a";
        const string room = "arena";
        const string player = "slow";

        var harness = new IntegrationHarness(_ => new MoveRightGame(), lifecycle: RoomLifecycle.Persist);
        var key = harness.Key(tenant, room);

        // A subscriber whose server-side writes block once armed (a stalled / very slow client).
        var slow = new GatedSendTransport(new ConnectionId($"{tenant}:{room}:{player}"));
        slow.ClientSend(Hello(tenant, room, player));
        slow.ClientSend(Join(tenant, room, player));
        var loop = harness.Server.HandleConnectionAsync(slow, Claims(tenant, room, player));

        // Arm the block only after the join has been processed (the welcome was already sent),
        // so the gate affects the tick fan-out, not connection setup.
        await WaitUntilAsync(() => harness.Server.ActiveRooms.Contains(key), TimeSpan.FromSeconds(2));
        slow.ArmBlock();

        var sw = Stopwatch.StartNew();
        var tickTask = harness.Server.TickRoom(key);
        var winner = await Task.WhenAny(tickTask, Task.Delay(TimeSpan.FromMilliseconds(750)));
        sw.Stop();
        var tickReturnedPromptly = winner == tickTask;

        // Release and drain so the test never hangs regardless of the assertion outcome.
        slow.Release();
        slow.CompleteClient();
        await loop;
        await tickTask;

        _output.WriteLine($"TickRoom returned in {sw.ElapsedMilliseconds}ms with one stalled subscriber (prompt: {tickReturnedPromptly})");

        Assert.True(
            tickReturnedPromptly,
            $"Gap #8: one stalled client blocked TickRoom for >{sw.ElapsedMilliseconds}ms. Fan-out in " +
            "RealtimeServer.TickRoom awaits each connection's SendAsync inline (head-of-line blocking); it must " +
            "instead hand the snapshot to a bounded per-connection IOutboundChannel and return without blocking on client I/O.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout)
            {
                throw new TimeoutException("Condition was not met within the timeout (connection never subscribed).");
            }

            await Task.Delay(10);
        }
    }

    private static MessageEnvelope Hello(string tenant, string room, string player) =>
        Envelope(MessageType.ClientHello, new ClientHello(player), tenant, room: null, player);

    private static MessageEnvelope Join(string tenant, string room, string player) =>
        Envelope(MessageType.ClientJoinRoom, new ClientJoinRoom(new RoomId(room)), tenant, room, player);

    private static JoinTokenClaims Claims(string tenant, string room, string player) =>
        new(tenant, "demo", room, player, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1));

    private static MessageEnvelope Envelope(MessageType type, IMessagePayload payload, string tenant, string? room, string player) =>
        new()
        {
            TenantId = new TenantId(tenant),
            GameId = new GameId("demo"),
            RoomId = room is null ? null : new RoomId(room),
            SessionId = null,
            PlayerId = new PlayerId(player),
            ProtocolVersion = ProtocolVersions.Current,
            MessageType = type,
            Sequence = 0,
            TraceId = $"{type}-{player}",
            Payload = payload,
        };

    /// <summary>
    /// A bidirectional transport whose server-side <see cref="SendAsync"/> blocks indefinitely
    /// once <see cref="ArmBlock"/> is called, until <see cref="Release"/>. Models a client whose
    /// socket has stopped draining. Inbound uses a channel, never wall-clock time.
    /// </summary>
    private sealed class GatedSendTransport : IBidirectionalTransport
    {
        private readonly Channel<MessageEnvelope> _inbound = Channel.CreateUnbounded<MessageEnvelope>();
        private volatile TaskCompletionSource? _block;

        public GatedSendTransport(ConnectionId connectionId) => ConnectionId = connectionId;

        public ConnectionId ConnectionId { get; }

        public void ArmBlock() => _block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _block?.TrySetResult();

        public async Task SendAsync(MessageEnvelope message, CancellationToken cancellationToken = default)
        {
            var gate = _block;
            if (gate is not null)
            {
                await gate.Task.ConfigureAwait(false);
            }
        }

        public async Task<MessageEnvelope?> ReceiveAsync(CancellationToken cancellationToken = default)
        {
            if (await _inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return _inbound.Reader.TryRead(out var message) ? message : null;
            }

            return null;
        }

        public void ClientSend(MessageEnvelope message) => _inbound.Writer.TryWrite(message);

        public void CompleteClient() => _inbound.Writer.TryComplete();
    }
}
