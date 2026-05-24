namespace GameServer.Observability;

/// <summary>Per-room aggregated stats for a measured value (e.g. that room's tick duration).</summary>
public sealed record RoomStat(string Room, long Count, double MeanMs, double MaxMs);

/// <summary>
/// Bounded-cardinality per-room telemetry. The global <see cref="AggregatingTelemetrySink"/>
/// folds tags away, so it can answer "what is the mean tick time?" but never "which room is
/// hot?". This keeps per-room aggregates while bounding cardinality: it tracks at most
/// <c>maxRooms</c> rooms, and when full it evicts the coldest (lowest peak) so the hottest
/// rooms — the ones an operator is hunting — are the ones retained.
/// </summary>
public sealed class RoomScopedMetrics
{
    private readonly int _maxRooms;
    private readonly object _lock = new();
    private readonly Dictionary<string, Accumulator> _byRoom = new();
    private long _evicted;

    public RoomScopedMetrics(int maxRooms = 1024)
    {
        if (maxRooms <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRooms), maxRooms, "Tracked-room cap must be positive.");
        }

        _maxRooms = maxRooms;
    }

    /// <summary>Number of rooms currently tracked.</summary>
    public int TrackedRooms
    {
        get { lock (_lock) { return _byRoom.Count; } }
    }

    /// <summary>How many rooms have been evicted under cardinality pressure (an observability gap signal).</summary>
    public long EvictedRooms
    {
        get { lock (_lock) { return _evicted; } }
    }

    public void Record(string room, double valueMs)
    {
        lock (_lock)
        {
            if (!_byRoom.TryGetValue(room, out var accumulator))
            {
                if (_byRoom.Count >= _maxRooms)
                {
                    EvictColdest();
                }

                accumulator = new Accumulator();
                _byRoom[room] = accumulator;
            }

            accumulator.Add(valueMs);
        }
    }

    /// <summary>The <paramref name="topN"/> hottest tracked rooms by peak value, descending.</summary>
    public IReadOnlyList<RoomStat> Hottest(int topN)
    {
        lock (_lock)
        {
            return _byRoom
                .Select(kv => new RoomStat(kv.Key, kv.Value.Count, kv.Value.Mean, kv.Value.Max))
                .OrderByDescending(s => s.MaxMs)
                .Take(topN)
                .ToList();
        }
    }

    private void EvictColdest()
    {
        var coldest = _byRoom.OrderBy(kv => kv.Value.Max).First().Key;
        _byRoom.Remove(coldest);
        _evicted++;
    }

    private sealed class Accumulator
    {
        private long _count;
        private double _sum;
        private double _max = double.NegativeInfinity;

        public long Count => _count;
        public double Mean => _count > 0 ? _sum / _count : 0;
        public double Max => _count > 0 ? _max : 0;

        public void Add(double value)
        {
            _count++;
            _sum += value;
            if (value > _max)
            {
                _max = value;
            }
        }
    }
}
