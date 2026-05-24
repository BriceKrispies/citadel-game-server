using System.Collections.Concurrent;
using System.Diagnostics;
using GameServer.Routing;

namespace GameServer.Transport;

/// <summary>One room's tick within a cycle: when it started (relative to cycle start) and how long it took.</summary>
public sealed record RoomTickSample(RoomKey Room, double StartOffsetMs, double ElapsedMs)
{
    public double EndOffsetMs => StartOffsetMs + ElapsedMs;
}

/// <summary>
/// The outcome of ticking every active room once. Carries the wall-clock cost of the
/// whole cycle plus a per-room timing sample, so the tick driver can report cadence
/// health and a scenario can prove whether rooms ticked concurrently or one-at-a-time.
/// </summary>
public sealed record RoomTickCycleReport(int RoomCount, double TotalElapsedMs, IReadOnlyList<RoomTickSample> Samples)
{
    /// <summary>
    /// Peak number of room ticks that were in flight at the same instant, derived from
    /// the sample intervals. 1 means strictly serial (head-of-line blocking); &gt;1 means
    /// rooms overlapped.
    /// </summary>
    public int MaxConcurrency => ComputeMaxConcurrency(Samples);

    private static int ComputeMaxConcurrency(IReadOnlyList<RoomTickSample> samples)
    {
        // Sweep line over interval endpoints: +1 at each start, -1 at each end, ordered
        // so an end at the same instant as a start is processed first (they don't overlap).
        var events = new List<(double Time, int Delta)>(samples.Count * 2);
        foreach (var s in samples)
        {
            events.Add((s.StartOffsetMs, +1));
            events.Add((s.EndOffsetMs, -1));
        }

        events.Sort((a, b) => a.Time != b.Time ? a.Time.CompareTo(b.Time) : a.Delta.CompareTo(b.Delta));

        int current = 0, max = 0;
        foreach (var (_, delta) in events)
        {
            current += delta;
            if (current > max)
            {
                max = current;
            }
        }

        return max;
    }
}

/// <summary>
/// Drives one tick of every active room. The strategy (serial vs concurrent) is the
/// whole point of this seam: rooms are independent state owners, so how they are
/// scheduled is a platform policy that should be swappable and provable, not baked into
/// the hosted timer loop. The delegate is responsible for its own error handling — both
/// schedulers let an exception propagate and abort the cycle.
/// </summary>
public interface IRoomTickScheduler
{
    Task<RoomTickCycleReport> TickCycleAsync(
        IReadOnlyCollection<RoomKey> rooms,
        Func<RoomKey, CancellationToken, Task> tickRoom,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Ticks rooms one at a time in enumeration order. This is the original behavior, kept
/// as an honest baseline: a single slow room delays every room behind it (head-of-line
/// blocking) and no second core is ever used.
/// </summary>
public sealed class SequentialRoomTickScheduler : IRoomTickScheduler
{
    public async Task<RoomTickCycleReport> TickCycleAsync(
        IReadOnlyCollection<RoomKey> rooms,
        Func<RoomKey, CancellationToken, Task> tickRoom,
        CancellationToken cancellationToken = default)
    {
        var cycleStart = Stopwatch.GetTimestamp();
        var samples = new List<RoomTickSample>(rooms.Count);

        foreach (var room in rooms)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = Stopwatch.GetTimestamp();
            await tickRoom(room, cancellationToken).ConfigureAwait(false);
            samples.Add(new RoomTickSample(
                room,
                Stopwatch.GetElapsedTime(cycleStart, start).TotalMilliseconds,
                Stopwatch.GetElapsedTime(start).TotalMilliseconds));
        }

        return new RoomTickCycleReport(rooms.Count, Stopwatch.GetElapsedTime(cycleStart).TotalMilliseconds, samples);
    }
}

/// <summary>
/// Ticks rooms concurrently with a bounded degree of parallelism. Rooms are independent
/// (distinct keys, locks, subscriber sets, replicators), so concurrent ticking is safe
/// and lets the authoritative loop use all cores — the cycle cost approaches that of the
/// single slowest room rather than the sum of all rooms.
/// </summary>
public sealed class ParallelRoomTickScheduler : IRoomTickScheduler
{
    private readonly int _maxDegreeOfParallelism;

    public ParallelRoomTickScheduler(int maxDegreeOfParallelism)
    {
        if (maxDegreeOfParallelism <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxDegreeOfParallelism), maxDegreeOfParallelism, "Degree of parallelism must be positive.");
        }

        _maxDegreeOfParallelism = maxDegreeOfParallelism;
    }

    /// <summary>A scheduler bounded to the machine's logical processor count — the production default.</summary>
    public static ParallelRoomTickScheduler ForProcessorCount() => new(Environment.ProcessorCount);

    public async Task<RoomTickCycleReport> TickCycleAsync(
        IReadOnlyCollection<RoomKey> rooms,
        Func<RoomKey, CancellationToken, Task> tickRoom,
        CancellationToken cancellationToken = default)
    {
        var cycleStart = Stopwatch.GetTimestamp();
        var samples = new ConcurrentBag<RoomTickSample>();
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = _maxDegreeOfParallelism,
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(rooms, options, async (room, token) =>
        {
            var start = Stopwatch.GetTimestamp();
            await tickRoom(room, token).ConfigureAwait(false);
            samples.Add(new RoomTickSample(
                room,
                Stopwatch.GetElapsedTime(cycleStart, start).TotalMilliseconds,
                Stopwatch.GetElapsedTime(start).TotalMilliseconds));
        }).ConfigureAwait(false);

        return new RoomTickCycleReport(rooms.Count, Stopwatch.GetElapsedTime(cycleStart).TotalMilliseconds, samples.ToList());
    }
}
