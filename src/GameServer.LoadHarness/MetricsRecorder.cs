using System.Collections.Concurrent;
using System.Threading;

namespace GameServer.LoadHarness;

/// <summary>Aggregated latency stats for one measurement category.</summary>
public sealed record LatencyStats(long Count, double MinMs, double MaxMs, double MeanMs, double P50Ms, double P95Ms, double P99Ms)
{
    public static readonly LatencyStats Empty = new(0, 0, 0, 0, 0, 0, 0);
}

/// <summary>An immutable point-in-time view of all recorded metrics.</summary>
public sealed record MetricsSnapshot
{
    public long AttemptedConnections { get; init; }
    public long SuccessfulConnections { get; init; }
    public long FailedConnections { get; init; }
    public long ActiveConnections { get; init; }
    public long MessagesSent { get; init; }
    public long MessagesReceived { get; init; }
    public long BytesSent { get; init; }
    public long BytesReceived { get; init; }
    public long ServerErrors { get; init; }
    public long UnexpectedCloses { get; init; }
    public long Corrections { get; init; }
    public long Reconnects { get; init; }
    public long MalformedFrameRejections { get; init; }
    public long MaxSnapshotLagTicks { get; init; }

    public LatencyStats ConnectLatency { get; init; } = LatencyStats.Empty;
    public LatencyStats HandshakeLatency { get; init; } = LatencyStats.Empty;
    public LatencyStats JoinLatency { get; init; } = LatencyStats.Empty;
    public LatencyStats InputToSnapshotLatency { get; init; } = LatencyStats.Empty;
}

/// <summary>
/// Thread-safe recorder for harness counters and latency samples. Safe to share
/// across thousands of concurrent virtual clients. Latency samples are retained for
/// percentile computation (acceptable at local scale; see LOAD_TESTING.md for the
/// reservoir-sampling gap at 50k).
/// </summary>
public sealed class MetricsRecorder
{
    private long _attempted, _successful, _failed, _active, _sent, _received, _bytesSent, _bytesReceived;
    private long _serverErrors, _unexpectedCloses, _corrections, _reconnects, _malformed, _maxSnapshotLag;

    private readonly Samples _connect = new();
    private readonly Samples _handshake = new();
    private readonly Samples _join = new();
    private readonly Samples _inputToSnapshot = new();

    public void IncrementAttemptedConnections() => Interlocked.Increment(ref _attempted);
    public void IncrementSuccessfulConnections() => Interlocked.Increment(ref _successful);
    public void IncrementFailedConnections() => Interlocked.Increment(ref _failed);
    public void IncrementActiveConnections() => Interlocked.Increment(ref _active);
    public void DecrementActiveConnections() => Interlocked.Decrement(ref _active);
    public void IncrementServerErrors() => Interlocked.Increment(ref _serverErrors);
    public void IncrementUnexpectedCloses() => Interlocked.Increment(ref _unexpectedCloses);
    public void IncrementCorrections() => Interlocked.Increment(ref _corrections);
    public void IncrementReconnects() => Interlocked.Increment(ref _reconnects);
    public void IncrementMalformedFrameRejections() => Interlocked.Increment(ref _malformed);

    public void RecordMessageSent(int bytes)
    {
        Interlocked.Increment(ref _sent);
        Interlocked.Add(ref _bytesSent, bytes);
    }

    public void RecordMessageReceived(int bytes)
    {
        Interlocked.Increment(ref _received);
        Interlocked.Add(ref _bytesReceived, bytes);
    }

    public void RecordConnectLatency(TimeSpan latency) => _connect.Add(latency.TotalMilliseconds);
    public void RecordHandshakeLatency(TimeSpan latency) => _handshake.Add(latency.TotalMilliseconds);
    public void RecordJoinLatency(TimeSpan latency) => _join.Add(latency.TotalMilliseconds);
    public void RecordInputToSnapshotLatency(TimeSpan latency) => _inputToSnapshot.Add(latency.TotalMilliseconds);

    public void RecordSnapshotLag(long lagTicks)
    {
        long current;
        do
        {
            current = Interlocked.Read(ref _maxSnapshotLag);
            if (lagTicks <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _maxSnapshotLag, lagTicks, current) != current);
    }

    public MetricsSnapshot Snapshot() => new()
    {
        AttemptedConnections = Interlocked.Read(ref _attempted),
        SuccessfulConnections = Interlocked.Read(ref _successful),
        FailedConnections = Interlocked.Read(ref _failed),
        ActiveConnections = Interlocked.Read(ref _active),
        MessagesSent = Interlocked.Read(ref _sent),
        MessagesReceived = Interlocked.Read(ref _received),
        BytesSent = Interlocked.Read(ref _bytesSent),
        BytesReceived = Interlocked.Read(ref _bytesReceived),
        ServerErrors = Interlocked.Read(ref _serverErrors),
        UnexpectedCloses = Interlocked.Read(ref _unexpectedCloses),
        Corrections = Interlocked.Read(ref _corrections),
        Reconnects = Interlocked.Read(ref _reconnects),
        MalformedFrameRejections = Interlocked.Read(ref _malformed),
        MaxSnapshotLagTicks = Interlocked.Read(ref _maxSnapshotLag),
        ConnectLatency = _connect.Compute(),
        HandshakeLatency = _handshake.Compute(),
        JoinLatency = _join.Compute(),
        InputToSnapshotLatency = _inputToSnapshot.Compute(),
    };

    private sealed class Samples
    {
        private readonly ConcurrentBag<double> _values = new();

        public void Add(double ms) => _values.Add(ms);

        public LatencyStats Compute()
        {
            var sorted = _values.ToArray();
            if (sorted.Length == 0)
            {
                return LatencyStats.Empty;
            }

            Array.Sort(sorted);
            var sum = 0.0;
            foreach (var v in sorted)
            {
                sum += v;
            }

            return new LatencyStats(
                sorted.Length,
                sorted[0],
                sorted[^1],
                sum / sorted.Length,
                Percentile(sorted, 0.50),
                Percentile(sorted, 0.95),
                Percentile(sorted, 0.99));
        }

        private static double Percentile(double[] sorted, double p)
        {
            var rank = (int)Math.Ceiling(p * sorted.Length) - 1;
            return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
        }
    }
}
