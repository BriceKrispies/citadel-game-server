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
        TextWriter console)
    {
        _config = config;
        _connectionFactory = connectionFactory;
        _tokens = tokens;
        _metrics = metrics;
        _failures = failures;
        _exporter = exporter;
        _clock = clock;
        _console = console;
    }

    public async Task<LoadResult> RunAsync(CancellationToken cancellationToken)
    {
        var rooms = RoomAssignmentStrategy.FromConfig(_config);
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

        _console.WriteLine($"Starting '{_config.Name}' ({_config.ScenarioType}): {_config.TotalClients} clients, ramp {_config.RampUpDuration}s, steady {_config.SteadyStateDuration}s");

        for (var i = 0; i < _config.TotalClients; i++)
        {
            var client = await CreateClientAsync(i, rooms, pattern, isMalformed: i < malformedCutoff, isSlowReceiver: i >= malformedCutoff && i < slowCutoff, lifetime.Token)
                .ConfigureAwait(false);
            clientTasks.Add(client.RunAsync(lifetime.Token));

            if (rampDelay > TimeSpan.Zero)
            {
                await Task.Delay(rampDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(_config.SteadyStateDuration), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down early
        }

        lifetime.Cancel();
        await Task.WhenAll(clientTasks).ConfigureAwait(false);
        await samplerTask.ConfigureAwait(false);

        var result = BuildResult();
        Export(result, timeSeries);
        _exporter.WriteConsoleSummary(result, _console);
        return result;
    }

    private async Task<VirtualClient> CreateClientAsync(
        int index, RoomAssignmentStrategy rooms, InputPattern pattern, bool isMalformed, bool isSlowReceiver, CancellationToken cancellationToken)
    {
        var playerId = $"{_config.Name}-p{index}";
        var room = rooms.AssignRoom(index);
        var token = await _tokens.GetTokenAsync(playerId, cancellationToken).ConfigureAwait(false);
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
    }
}
