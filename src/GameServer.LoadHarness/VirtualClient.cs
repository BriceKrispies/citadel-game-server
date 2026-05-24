using GameServer.Protocol.Realtime.V1;

namespace GameServer.LoadHarness;

/// <summary>
/// One simulated game client speaking the real binary protobuf protocol. Lifecycle:
/// connect → ClientHello → join room → stream ClientInputFrame (monotonic sequence)
/// → receive snapshots/deltas/corrections/errors/pongs → ack → close. Each client
/// runs independently, so a slow or failing client never blocks the others.
/// </summary>
public sealed class VirtualClient
{
    private readonly ScenarioConfig _config;
    private readonly IWebSocketConnection _connection;
    private readonly ProtocolClient _protocol;
    private readonly InputPattern _inputPattern;
    private readonly MetricsRecorder _metrics;
    private readonly FailureRecorder _failures;
    private readonly ILoadClock _clock;
    private readonly string _joinToken;

    private long _sequence;
    private long _clientTick;
    private long _handshakeStart;
    private long _joinStart;
    private long? _lastInputTimestamp;
    private ulong _lastServerTick;
    private ulong _lastAckedServerTick;
    private bool _awaitingJoinSnapshot;
    private bool _closing;

    public VirtualClient(
        int id,
        string playerId,
        string room,
        string joinToken,
        ScenarioConfig config,
        IWebSocketConnection connection,
        InputPattern inputPattern,
        MetricsRecorder metrics,
        FailureRecorder failures,
        ILoadClock clock,
        bool isMalformed = false,
        bool isSlowReceiver = false)
    {
        Id = id;
        PlayerId = playerId;
        Room = room;
        IsMalformed = isMalformed;
        IsSlowReceiver = isSlowReceiver;
        _joinToken = joinToken;
        _config = config;
        _connection = connection;
        _protocol = new ProtocolClient(config.TenantId, config.GameId, playerId);
        _inputPattern = inputPattern;
        _metrics = metrics;
        _failures = failures;
        _clock = clock;
    }

    public int Id { get; }
    public string PlayerId { get; }
    public string Room { get; }
    public bool IsMalformed { get; }
    public bool IsSlowReceiver { get; }

    private long NextSequence() => Interlocked.Increment(ref _sequence);

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _metrics.IncrementAttemptedConnections();
        var start = _clock.GetTimestamp();
        try
        {
            await _connection.ConnectAsync(_config.RealtimeUri(_joinToken), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _metrics.IncrementFailedConnections();
            _failures.Record(Id, FailureCategory.ConnectFailed, ex.Message);
            throw;
        }

        _metrics.RecordConnectLatency(_clock.GetElapsed(start));
        _metrics.IncrementSuccessfulConnections();
        _metrics.IncrementActiveConnections();
    }

    public async Task SendHelloAsync(CancellationToken cancellationToken)
    {
        _handshakeStart = _clock.GetTimestamp();
        await SendAsync(_protocol.Hello(_joinToken, NextSequence()), cancellationToken).ConfigureAwait(false);
    }

    public async Task SendJoinAsync(CancellationToken cancellationToken)
    {
        _joinStart = _clock.GetTimestamp();
        _awaitingJoinSnapshot = true;
        await SendAsync(_protocol.JoinRoom(Room, NextSequence()), cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> SendCommandAsync(CancellationToken cancellationToken)
    {
        var sequence = NextSequence();
        var tick = Interlocked.Increment(ref _clientTick);
        _lastInputTimestamp = _clock.GetTimestamp();
        await SendAsync(_protocol.Command(Room, tick, sequence, _config.Command), cancellationToken).ConfigureAwait(false);
        return sequence;
    }

    public Task SendPingAsync(CancellationToken cancellationToken) =>
        SendAsync(_protocol.Ping(Room, Interlocked.Read(ref _clientTick), NextSequence()), cancellationToken);

    public Task SendAckAsync(CancellationToken cancellationToken) =>
        SendAsync(_protocol.Ack(Room, (ulong)Interlocked.Read(ref _sequence), _lastServerTick, NextSequence()), cancellationToken);

    public Task SendMalformedAsync(CancellationToken cancellationToken) =>
        SendAsync(_protocol.MalformedFrame(), cancellationToken);

    /// <summary>Receives and handles one frame. Returns false if the connection closed.</summary>
    public async Task<bool> ReceiveOnceAsync(CancellationToken cancellationToken)
    {
        var frame = await _connection.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        if (frame.Kind == ConnectionFrameKind.Closed)
        {
            // A close during a requested shutdown is graceful, not a failure.
            if (!_closing && !cancellationToken.IsCancellationRequested)
            {
                _metrics.IncrementUnexpectedCloses();
                _failures.RecordUnexpectedClose(Id, "connection closed unexpectedly");
            }

            return false;
        }

        HandleBinary(frame.Payload);
        return true;
    }

    public async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!await ReceiveOnceAsync(cancellationToken).ConfigureAwait(false))
            {
                break;
            }
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        _closing = true;
        await _connection.CloseAsync(cancellationToken).ConfigureAwait(false);
        _metrics.DecrementActiveConnections();
    }

    /// <summary>Full real-run lifecycle. Paced by the cancellation token (set by the scenario runner).</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ConnectAsync(cancellationToken).ConfigureAwait(false);

            if (IsMalformed)
            {
                await SendMalformedAsync(cancellationToken).ConfigureAwait(false);
                await DrainUntilCancelledAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            await SendHelloAsync(cancellationToken).ConfigureAwait(false);
            var receiveTask = ReceiveLoopAsync(cancellationToken);
            await SendJoinAsync(cancellationToken).ConfigureAwait(false);
            await RunInputLoopAsync(cancellationToken).ConfigureAwait(false);
            await receiveTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected at end of steady state
        }
        catch (Exception ex)
        {
            _failures.Record(Id, FailureCategory.ReceiveError, ex.Message);
        }
        finally
        {
            await SafeCloseAsync().ConfigureAwait(false);
        }
    }

    private async Task RunInputLoopAsync(CancellationToken cancellationToken)
    {
        if (!_inputPattern.SendsInput)
        {
            await DrainUntilCancelledAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        long sent = 0;
        var pingEvery = _config.PingInterval > 0 ? _config.PingInterval : double.PositiveInfinity;
        var nextPingAt = pingEvery;
        var elapsed = 0.0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var delay = _inputPattern.DelayBefore(sent);
            if (delay > TimeSpan.Zero && delay != Timeout.InfiniteTimeSpan)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                elapsed += delay.TotalSeconds;
            }

            await SendCommandAsync(cancellationToken).ConfigureAwait(false);
            sent++;

            if (elapsed >= nextPingAt)
            {
                await SendPingAsync(cancellationToken).ConfigureAwait(false);
                nextPingAt += pingEvery;
            }
        }
    }

    private async Task DrainUntilCancelledAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ReceiveLoopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected
        }
    }

    private void HandleBinary(byte[] payload)
    {
        _metrics.RecordMessageReceived(payload.Length);

        RealtimeEnvelope env;
        try
        {
            env = _protocol.Parse(payload);
        }
        catch (Exception ex)
        {
            _failures.Record(Id, FailureCategory.ReceiveError, ex.Message);
            return;
        }

        switch (env.PayloadCase)
        {
            case RealtimeEnvelope.PayloadOneofCase.ServerWelcome:
                _metrics.RecordHandshakeLatency(_clock.GetElapsed(_handshakeStart));
                break;

            case RealtimeEnvelope.PayloadOneofCase.ServerSnapshot:
                OnSnapshot(env.ServerTick == 0 ? env.ServerSnapshot.ServerTick : env.ServerTick);
                break;

            case RealtimeEnvelope.PayloadOneofCase.ServerDelta:
                OnSnapshot(env.ServerDelta.ToServerTick);
                break;

            case RealtimeEnvelope.PayloadOneofCase.ServerCorrection:
                _metrics.IncrementCorrections();
                break;

            case RealtimeEnvelope.PayloadOneofCase.ServerError:
                _metrics.IncrementServerErrors();
                _failures.Record(Id, FailureCategory.ServerError, env.ServerError.Code.ToString());
                if (env.ServerError.Code == ErrorCode.MalformedFrame)
                {
                    _metrics.IncrementMalformedFrameRejections();
                    _failures.Record(Id, FailureCategory.MalformedRejected, env.ServerError.Message);
                }

                break;

            case RealtimeEnvelope.PayloadOneofCase.ServerPong:
            case RealtimeEnvelope.PayloadOneofCase.ServerRoomJoined:
            default:
                break;
        }
    }

    private void OnSnapshot(ulong serverTick)
    {
        if (_awaitingJoinSnapshot)
        {
            _metrics.RecordJoinLatency(_clock.GetElapsed(_joinStart));
            // First snapshot after join: this client is now confirmed receiving room updates.
            _metrics.RecordRoomClientReceiving(Room);
            _awaitingJoinSnapshot = false;
        }

        if (_lastInputTimestamp is { } inputAt)
        {
            _metrics.RecordInputToSnapshotLatency(_clock.GetElapsed(inputAt));
            _lastInputTimestamp = null;
        }

        if (serverTick > _lastServerTick)
        {
            _lastServerTick = serverTick;
        }

        var lag = (long)(_lastServerTick - _lastAckedServerTick);
        _metrics.RecordSnapshotLag(lag);
        _metrics.RecordRoomSnapshot(Room, serverTick, lag);

        if (_config.SnapshotAckMode == SnapshotAckMode.EverySnapshot)
        {
            _lastAckedServerTick = _lastServerTick;
        }
    }

    private async Task SendAsync(byte[] payload, CancellationToken cancellationToken)
    {
        await _connection.SendBinaryAsync(payload, cancellationToken).ConfigureAwait(false);
        _metrics.RecordMessageSent(payload.Length);
    }

    private async Task SafeCloseAsync()
    {
        try
        {
            await CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // best-effort close
        }
    }
}
