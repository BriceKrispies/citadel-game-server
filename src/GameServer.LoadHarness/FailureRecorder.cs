using System.Collections.Concurrent;

namespace GameServer.LoadHarness;

/// <summary>Categories of failure the harness distinguishes for diagnosis.</summary>
public enum FailureCategory
{
    ConnectFailed,
    HandshakeFailed,
    UnexpectedClose,
    ServerError,
    MalformedRejected,
    ReceiveError,
}

/// <summary>One recorded failure, with enough context to triage after a run.</summary>
public sealed record FailureEntry(int ClientId, FailureCategory Category, string Detail);

/// <summary>
/// Thread-safe structured failure log, complementary to <see cref="MetricsRecorder"/>'s
/// numeric counters. Keeps a bounded sample of entries plus per-category counts so a
/// run can be triaged without exploding memory at scale.
/// </summary>
public sealed class FailureRecorder
{
    private const int MaxRetainedEntries = 1000;

    private readonly ConcurrentQueue<FailureEntry> _entries = new();
    private readonly ConcurrentDictionary<FailureCategory, long> _counts = new();
    private long _entryCount;

    public void Record(int clientId, FailureCategory category, string detail)
    {
        _counts.AddOrUpdate(category, 1, (_, current) => current + 1);

        if (Interlocked.Increment(ref _entryCount) <= MaxRetainedEntries)
        {
            _entries.Enqueue(new FailureEntry(clientId, category, detail));
        }
    }

    public void RecordUnexpectedClose(int clientId, string detail) =>
        Record(clientId, FailureCategory.UnexpectedClose, detail);

    public long CountOf(FailureCategory category) => _counts.TryGetValue(category, out var c) ? c : 0;

    public long TotalCount => Interlocked.Read(ref _entryCount);

    public IReadOnlyList<FailureEntry> Entries => _entries.ToArray();

    public IReadOnlyDictionary<FailureCategory, long> Counts =>
        _counts.ToDictionary(kv => kv.Key, kv => kv.Value);
}
