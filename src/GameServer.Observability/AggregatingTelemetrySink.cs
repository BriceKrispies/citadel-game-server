using System.Collections.Concurrent;

namespace GameServer.Observability;

/// <summary>Aggregated stats for a measured metric (e.g. tick_duration_ms).</summary>
public sealed record MeasureStats(long Count, double Sum, double Min, double Max)
{
    public double Mean => Count > 0 ? Sum / Count : 0;

    public static readonly MeasureStats Empty = new(0, 0, 0, 0);
}

/// <summary>An immutable view of the aggregated telemetry at a point in time.</summary>
public sealed record TelemetrySnapshot(
    IReadOnlyDictionary<string, long> Counters,
    IReadOnlyDictionary<string, MeasureStats> Measures,
    IReadOnlyDictionary<string, long> Events)
{
    public long Counter(string name) => Counters.TryGetValue(name, out var v) ? v : 0;

    public MeasureStats Measure(string name) => Measures.TryGetValue(name, out var v) ? v : MeasureStats.Empty;

    public long EventCount(string name) => Events.TryGetValue(name, out var v) ? v : 0;
}

/// <summary>
/// In-memory aggregating telemetry sink. Every call is an O(1), allocation-free
/// accumulation (a counter add or a min/max/sum update) — it never performs I/O on
/// the hot path, so the realtime loop can emit millions of times per second. A
/// separate flush loop periodically reads <see cref="Snapshot"/> and reports rolling
/// rates/percentiles. Aggregation is by metric/event NAME (tags are summed over) to
/// keep cardinality bounded.
/// </summary>
public sealed class AggregatingTelemetrySink : ITelemetrySink
{
    private readonly ConcurrentDictionary<string, long> _counters = new();
    private readonly ConcurrentDictionary<string, Accumulator> _measures = new();
    private readonly ConcurrentDictionary<string, long> _events = new();

    public void Increment(string metric, IReadOnlyDictionary<string, string>? tags = null) =>
        _counters.AddOrUpdate(metric, 1, static (_, current) => current + 1);

    public void Measure(string metric, double value, IReadOnlyDictionary<string, string>? tags = null) =>
        _measures.GetOrAdd(metric, static _ => new Accumulator()).Add(value);

    public void Event(string name, IReadOnlyDictionary<string, string>? fields = null) =>
        _events.AddOrUpdate(name, 1, static (_, current) => current + 1);

    public TelemetrySnapshot Snapshot() => new(
        _counters.ToDictionary(kv => kv.Key, kv => kv.Value),
        _measures.ToDictionary(kv => kv.Key, kv => kv.Value.ToStats()),
        _events.ToDictionary(kv => kv.Key, kv => kv.Value));

    private sealed class Accumulator
    {
        private readonly object _lock = new();
        private long _count;
        private double _sum;
        private double _min = double.PositiveInfinity;
        private double _max = double.NegativeInfinity;

        public void Add(double value)
        {
            lock (_lock)
            {
                _count++;
                _sum += value;
                if (value < _min)
                {
                    _min = value;
                }

                if (value > _max)
                {
                    _max = value;
                }
            }
        }

        public MeasureStats ToStats()
        {
            lock (_lock)
            {
                return _count == 0
                    ? MeasureStats.Empty
                    : new MeasureStats(_count, _sum, _min, _max);
            }
        }
    }
}
