using System.Collections.Concurrent;

namespace GameServer.LoadHarness;

/// <summary>
/// Orchestrates one harness instance's scenario: ramps virtual clients up over the
/// configured window, holds steady-state load, samples a time-series, then tears
/// down and exports results. Each client runs on its own task, so failures and slow
/// receivers are isolated. Multiple instances run this independently to reach 50k.
/// </summary>
public sealed class ScenarioRunner
{
    private readonly ScenarioConfig _config;
    private readonly Func<IWebSocketConnection> _connectionFactory;
    private readonly JoinTokenProvider _tokens;
    private readonly ServerRoomVerifier? _verifier;
    private readonly MetricsRecorder _metrics;
    private readonly FailureRecorder _failures;
    private readonly ResultExporter _exporter;
    private readonly ILoadClock _clock;
    private readonly TextWriter _console;

    public ScenarioRunner(
        ScenarioConfig config,
        Func<IWebSocketConnection> connectionFactory,
        JoinTokenProvider tokens,
        MetricsRecorder metrics,
        FailureRecorder failures,
        ResultExporter exporter,
        ILoadClock clock,
        TextWriter console,
        ServerRoomVerifier? verifier = null)
    {
        _config = config;
        _connectionFactory = connectionFactory;
        _tokens = tokens;
        _verifier = verifier;
        _metrics = metrics;
        _failures = failures;
        _exporter = exporter;
        _clock = clock;
        _console = console;
    }

    public async Task<LoadResult> RunAsync(CancellationToken cancellationToken)
    {
        // Provision the rooms up front and assign clients across the REAL ids the control
        // plane returned. Each client's token is then minted for the exact room it joins, so
        // the join is not rejected as a token/room mismatch (RealtimeServer enforces this).
        var roomIds = await _tokens.ProvisionRoomsAsync(_config.RoomCount, cancellationToken).ConfigureAwait(false);
        var rooms = new RoomAssignmentStrategy(roomIds);
        var pattern = InputPattern.FromConfig(_config);
        var malformedCutoff = (int)(_config.TotalClients * _config.MalformedClientPercent / 100.0);
        var slowCutoff = malformedCutoff + (int)(_config.TotalClients * _config.SlowReceiverPercent / 100.0);

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeSeries = new ConcurrentQueue<TimeSeriesSample>();
        var startTimestamp = _clock.GetTimestamp();
        var samplerTask = SampleLoopAsync(startTimestamp, timeSeries, lifetime.Token);

        var clientTasks = new List<Task>(_config.TotalClients);
        var rampDelay = _config.TotalClients > 0
            ? TimeSpan.FromSeconds(_config.RampUpDuration / _config.TotalClients)
            : TimeSpan.Zero;

        // Bound how many client setups run at once so a slow control plane does not serialize the
        // ramp (each join-token mint is independent), while launches stay paced across the window.
        using var startupGate = new SemaphoreSlim(Math.Max(1, Math.Min(_config.MaxConcurrentStartups, Math.Max(1, _config.TotalClients))));

        _console.WriteLine($"Starting '{_config.Name}' ({_config.ScenarioType}): {_config.TotalClients} clients, ramp {_config.RampUpDuration}s, steady {_config.SteadyStateDuration}s");

        // Pace launches against a cumulative schedule rather than a fixed per-client sleep: a 10k
        // ramp implies ~6ms spacing, far below the OS timer granularity, so per-iteration delays
        // would round up and stretch the ramp severalfold. Instead we sleep only when ahead of the
        // schedule, so one coarse sleep covers many clients' worth of spacing and the total ramp
        // tracks RampUpDuration regardless of timer resolution.
        var rampStart = _clock.GetTimestamp();
        for (var i = 0; i < _config.TotalClients; i++)
        {
            await startupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var index = i;
            var malformed = index < malformedCutoff;
            var slow = index >= malformedCutoff && index < slowCutoff;
            clientTasks.Add(RunClientAsync(index, rooms, pattern, malformed, slow, startupGate, lifetime.Token));

            if (rampDelay > TimeSpan.Zero)
            {
                var aheadBy = rampDelay * (i + 1) - _clock.GetElapsed(rampStart);
                if (aheadBy > TimeSpan.FromMilliseconds(1))
                {
                    await Task.Delay(aheadBy, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        ServerVerification verification;
        try
        {
            // Hold steady, then near the end ask the server itself whether every room is full
            // and live. We poll twice a beat apart so we can prove the tick is advancing.
            var steady = TimeSpan.FromSeconds(_config.SteadyStateDuration);
            var verifyAt = steady - TimeSpan.FromSeconds(Math.Min(2, _config.SteadyStateDuration / 2.0));
            await Task.Delay(verifyAt > TimeSpan.Zero ? verifyAt : TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
            verification = await VerifyServerRoomsAsync(rooms, cancellationToken).ConfigureAwait(false);
            await Task.Delay(steady - verifyAt, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            verification = ServerVerification.NotRun;
        }

        lifetime.Cancel();
        await Task.WhenAll(clientTasks).ConfigureAwait(false);
        await samplerTask.ConfigureAwait(false);

        var result = BuildResult() with { ServerVerification = verification };
        Export(result, timeSeries);
        _exporter.WriteConsoleSummary(result, _console);
        return result;
    }

    // Asks the server (admin API) for its authoritative view of the rooms, twice a second
    // apart, and folds the two readings into a per-room verdict (full + tick advancing).
    private async Task<ServerVerification> VerifyServerRoomsAsync(RoomAssignmentStrategy rooms, CancellationToken cancellationToken)
    {
        if (_verifier is null)
        {
            return ServerVerification.NotRun;
        }

        var expectedRooms = rooms.AllRooms();
        var expectedPerRoom = ExpectedSubscribersPerRoom(expectedRooms.Count);

        try
        {
            var first = (await _verifier.ObserveRoomsAsync(cancellationToken).ConfigureAwait(false))
                .ToDictionary(r => r.RoomId);
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            var second = (await _verifier.ObserveRoomsAsync(cancellationToken).ConfigureAwait(false))
                .ToDictionary(r => r.RoomId);

            var checks = new List<ServerRoomCheck>(expectedRooms.Count);
            foreach (var roomId in expectedRooms)
            {
                var hasFirst = first.TryGetValue(roomId, out var a);
                var hasSecond = second.TryGetValue(roomId, out var b);
                if (!hasFirst && !hasSecond)
                {
                    continue; // server never reported this room
                }

                var subscribers = hasSecond ? b!.SubscriberCount : a!.SubscriberCount;
                var tickStart = hasFirst ? a!.Tick : 0;
                var tickEnd = hasSecond ? b!.Tick : tickStart;
                checks.Add(new ServerRoomCheck(roomId, subscribers, tickStart, tickEnd));
            }

            return new ServerVerification(expectedRooms.Count, expectedPerRoom, checks);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _console.WriteLine($"  server verification skipped: {ex.Message}");
            return ServerVerification.NotRun;
        }
    }

    // Round-robin assignment gives the lower bound (totalClients / roomCount) to every room;
    // the first (totalClients % roomCount) rooms get one extra. We verify the common floor.
    private int ExpectedSubscribersPerRoom(int roomCount) =>
        roomCount > 0 ? _config.TotalClients / roomCount : 0;

    // One client's full life: setup (token mint) under the startup gate, then run unbounded for
    // the rest of the scenario. The gate is released as soon as setup finishes — not when the
    // client stops — so the cap limits concurrent SETUP, not the total live client count. A
    // setup failure is recorded and isolated; it never faults the whole run.
    private async Task RunClientAsync(
        int index, RoomAssignmentStrategy rooms, InputPattern pattern, bool malformed, bool slow, SemaphoreSlim startupGate, CancellationToken cancellationToken)
    {
        VirtualClient client;
        try
        {
            client = await CreateClientAsync(index, rooms, pattern, malformed, slow, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _failures.Record(index, FailureCategory.ConnectFailed, $"setup failed: {ex.Message}");
            return;
        }
        finally
        {
            startupGate.Release();
        }

        await client.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<VirtualClient> CreateClientAsync(
        int index, RoomAssignmentStrategy rooms, InputPattern pattern, bool isMalformed, bool isSlowReceiver, CancellationToken cancellationToken)
    {
        var playerId = $"{_config.Name}-p{index}";
        var room = rooms.AssignRoom(index);
        var token = await _tokens.GetTokenAsync(playerId, room, cancellationToken).ConfigureAwait(false);
        return new VirtualClient(index, playerId, room, token, _config, _connectionFactory(), pattern, _metrics, _failures, _clock, isMalformed, isSlowReceiver);
    }

    private async Task SampleLoopAsync(long startTimestamp, ConcurrentQueue<TimeSeriesSample> series, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                var m = _metrics.Snapshot();
                series.Enqueue(new TimeSeriesSample(
                    _clock.GetElapsed(startTimestamp).TotalSeconds,
                    m.ActiveConnections, m.MessagesSent, m.MessagesReceived, m.BytesSent, m.BytesReceived, m.ServerErrors, m.UnexpectedCloses));
                _console.WriteLine($"  t+{_clock.GetElapsed(startTimestamp).TotalSeconds:0}s active={m.ActiveConnections} sent={m.MessagesSent} recv={m.MessagesReceived} errors={m.ServerErrors}");
            }
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
    }

    private LoadResult BuildResult() => new(
        _config.Name,
        _config.ScenarioType,
        _config.TotalClients,
        _metrics.Snapshot(),
        _failures.Counts.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value));

    private void Export(LoadResult result, IEnumerable<TimeSeriesSample> series)
    {
        var dir = _config.OutputDirectory;
        _exporter.WriteJson(result, Path.Combine(dir, $"{_config.Name}.result.json"));
        _exporter.WriteCsv(series, Path.Combine(dir, $"{_config.Name}.timeseries.csv"));
        _exporter.WritePerRoomCsv(result.Metrics.PerRoom, Path.Combine(dir, $"{_config.Name}.per-room.csv"));
    }
}
