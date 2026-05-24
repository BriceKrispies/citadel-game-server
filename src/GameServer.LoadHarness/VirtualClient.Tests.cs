using GameServer.LoadHarness.Testing;
using GameServer.Protocol.Realtime;
using GameServer.Protocol.Realtime.V1;
using Xunit;

namespace GameServer.LoadHarness;

public sealed class VirtualClientTests
{
    private static readonly RealtimeProtobufCodec Codec = new();

    private static ScenarioConfig Config() => new()
    {
        TenantId = "tenant-a",
        GameId = "demo-game",
        InputRatePerClientPerSecond = 2,
        SnapshotAckMode = SnapshotAckMode.EverySnapshot,
    };

    private static VirtualClient NewClient(
        int id, FakeWebSocketConnection connection, MetricsRecorder metrics, FailureRecorder failures,
        bool malformed = false)
    {
        var config = Config();
        return new VirtualClient(
            id, $"p{id}", "room-1", "jt", config, connection,
            InputPattern.FromConfig(config), metrics, failures, new FakeLoadClock(),
            isMalformed: malformed);
    }

    private static byte[] ServerSnapshotFrame(ulong tick)
    {
        var env = new RealtimeEnvelope
        {
            ProtocolVersion = ProtocolVersion.V1,
            MessageType = MessageType.ServerSnapshot,
            TenantId = "tenant-a",
            GameId = "demo-game",
            PlayerId = "p",
            TraceId = "t",
            ServerTick = tick,
            ServerSnapshot = new ServerSnapshot { ServerTick = tick },
        };
        return Codec.Encode(env);
    }

    [Fact]
    public async Task VirtualClient_SendsForwardableCommandsWithMonotonicSequence()
    {
        // Load MUST be driven by ClientCommand (the message the server forwards to the room),
        // not ClientInputFrame (which the platform discards and does not count as liveness —
        // a client sending only input frames is reaped at the idle deadline).
        var connection = new FakeWebSocketConnection();
        var client = NewClient(0, connection, new MetricsRecorder(), new FailureRecorder());

        for (var i = 0; i < 5; i++)
        {
            await client.SendCommandAsync(CancellationToken.None);
        }

        var commands = connection.Sent
            .Select(Codec.Decode)
            .Where(e => e.PayloadCase == RealtimeEnvelope.PayloadOneofCase.ClientCommand)
            .ToList();

        Assert.Equal(5, commands.Count);
        Assert.All(commands, c => Assert.Equal("MoveRight", c.ClientCommand.Command));
        for (var i = 1; i < commands.Count; i++)
        {
            Assert.True(commands[i].Sequence > commands[i - 1].Sequence, "envelope sequence must strictly increase");
            Assert.True(commands[i].ClientTick > commands[i - 1].ClientTick, "client tick must strictly increase");
        }
    }

    [Fact]
    public async Task SlowReceiverScenario_DoesNotBlockOtherVirtualClients()
    {
        using var cts = new CancellationTokenSource();
        var snapshot = ServerSnapshotFrame(1);

        // A slow receiver whose connection blocks on receive.
        var slowConn = new FakeWebSocketConnection();
        var slowGate = slowConn.BlockReceive();
        var slowClient = NewClient(0, slowConn, new MetricsRecorder(), new FailureRecorder());

        // Fast receivers with a frame queued then a clean close.
        var fastTasks = new List<Task>();
        for (var i = 1; i <= 3; i++)
        {
            var conn = new FakeWebSocketConnection();
            conn.EnqueueInbound(snapshot);
            conn.CompleteInbound();
            var client = NewClient(i, conn, new MetricsRecorder(), new FailureRecorder());
            fastTasks.Add(client.ReceiveLoopAsync(cts.Token));
        }

        var slowTask = slowClient.ReceiveLoopAsync(cts.Token);

        await Task.WhenAll(fastTasks); // completes despite the slow client being blocked

        Assert.All(fastTasks, t => Assert.True(t.IsCompletedSuccessfully));
        Assert.False(slowTask.IsCompleted);

        // Releasing the slow client lets it finish too.
        slowGate.SetResult();
        slowConn.CompleteInbound();
        await slowTask;
    }

    [Fact]
    public async Task MalformedClientScenario_SendsInvalidFrameAndRecordsServerError()
    {
        var connection = new FakeWebSocketConnection();
        var metrics = new MetricsRecorder();
        var failures = new FailureRecorder();
        var client = NewClient(0, connection, metrics, failures, malformed: true);

        await client.SendMalformedAsync(CancellationToken.None);

        // The server answers a malformed frame with a typed ServerError.
        var error = new RealtimeEnvelope
        {
            ProtocolVersion = ProtocolVersion.V1,
            MessageType = MessageType.ServerError,
            TenantId = "tenant-a",
            GameId = "demo-game",
            TraceId = "t",
            ServerError = new ServerError { Code = ErrorCode.MalformedFrame, Message = "Malformed binary frame" },
        };
        connection.EnqueueInbound(Codec.Encode(error));
        connection.CompleteInbound();

        await client.ReceiveOnceAsync(CancellationToken.None);

        var snapshot = metrics.Snapshot();
        Assert.Single(connection.Sent); // the malformed frame was actually sent
        Assert.True(snapshot.ServerErrors >= 1);
        Assert.True(snapshot.MalformedFrameRejections >= 1);
        Assert.Equal(1, failures.CountOf(FailureCategory.MalformedRejected));
    }
}
