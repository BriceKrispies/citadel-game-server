using GameServer.Protocol;
using GameServer.Routing;
using Xunit;

namespace GameServer.Transport;

/// <summary>
/// Tests the tick-scheduling seam. The concurrency assertions are made deterministic by
/// coordinating the tick delegates with gates rather than by sleeping for wall-clock
/// durations: the parallel scheduler is required to admit enough work simultaneously to
/// release a barrier, while the sequential scheduler is required to keep a live counter
/// at one. No real time is depended on for correctness.
/// </summary>
public sealed class RoomTickSchedulerTests
{
    private static IReadOnlyCollection<RoomKey> Rooms(int n) =>
        Enumerable.Range(0, n).Select(i => new RoomKey(new TenantId("t"), new RoomId($"r{i}"))).ToArray();

    [Fact]
    public async Task Sequential_RunsExactlyOneRoomAtATime()
    {
        var live = 0;
        var maxLive = 0;

        var report = await new SequentialRoomTickScheduler().TickCycleAsync(Rooms(6), async (_, _) =>
        {
            var now = Interlocked.Increment(ref live);
            maxLive = Math.Max(maxLive, now);
            await Task.Yield();
            Interlocked.Decrement(ref live);
        });

        Assert.Equal(1, maxLive);
        Assert.Equal(1, report.MaxConcurrency);
        Assert.Equal(6, report.Samples.Count);
    }

    [Fact]
    public async Task Sequential_NeverReportsPhantomConcurrency_ForManyTinyTicks()
    {
        // Regression for a sub-microsecond timing flake: when ticks are near-instant, the
        // recorded sample intervals must never round into a phantom overlap. A strictly
        // serial scheduler must report concurrency 1 for any tick body, no matter how short.
        // 200 rooms of effectively-zero work is the worst case for endpoint rounding.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var report = await new SequentialRoomTickScheduler().TickCycleAsync(Rooms(200), (_, _) => Task.CompletedTask);
            Assert.Equal(1, report.MaxConcurrency);
            Assert.Equal(200, report.Samples.Count);
        }
    }

    [Fact]
    public async Task Sequential_PreservesEnumerationOrder()
    {
        var order = new List<string>();

        await new SequentialRoomTickScheduler().TickCycleAsync(Rooms(4), (room, _) =>
        {
            order.Add(room.RoomId.Value);
            return Task.CompletedTask;
        });

        Assert.Equal(new[] { "r0", "r1", "r2", "r3" }, order);
    }

    [Fact]
    public async Task Parallel_AdmitsRoomsConcurrently_UpToTheConfiguredDegree()
    {
        const int degree = 4;
        var arrived = 0;
        var allArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Each tick blocks until `degree` ticks have arrived. This can only complete if
        // the scheduler runs at least `degree` of them at once; a serial scheduler would
        // deadlock here (and the WaitAsync timeout would fail the test rather than hang).
        var report = await new ParallelRoomTickScheduler(degree).TickCycleAsync(Rooms(degree), async (_, token) =>
        {
            if (Interlocked.Increment(ref arrived) == degree)
            {
                allArrived.SetResult();
            }

            await allArrived.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        });

        Assert.Equal(degree, report.MaxConcurrency);
        Assert.Equal(degree, report.Samples.Count);
    }

    [Fact]
    public async Task Parallel_RespectsTheConcurrencyBound()
    {
        const int degree = 2;
        var live = 0;
        var maxLive = 0;
        var gate = new SemaphoreSlim(0);
        var releasedAll = false;

        // Hold each tick until released, observing peak concurrency. Release is driven
        // from a watcher so we never block the whole pool indefinitely.
        var watcher = Task.Run(async () =>
        {
            while (!releasedAll)
            {
                await Task.Delay(5);
                if (Volatile.Read(ref live) >= degree)
                {
                    releasedAll = true;
                    gate.Release(100);
                }
            }
        });

        var report = await new ParallelRoomTickScheduler(degree).TickCycleAsync(Rooms(8), async (_, token) =>
        {
            var now = Interlocked.Increment(ref live);
            maxLive = Math.Max(maxLive, now);
            await gate.WaitAsync(token);
            Interlocked.Decrement(ref live);
        });

        await watcher;
        Assert.True(maxLive <= degree, $"observed {maxLive} concurrent ticks, bound was {degree}");
        Assert.Equal(8, report.Samples.Count);
    }

    [Fact]
    public void Parallel_RequiresPositiveDegree()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParallelRoomTickScheduler(0));
    }

    [Fact]
    public void Report_MaxConcurrency_FromOverlappingIntervals()
    {
        var room = new RoomKey(new TenantId("t"), new RoomId("r"));
        // Three intervals (start,end): [0,10], [5,15], [20,30] -> peak overlap of 2 (first two).
        var report = new RoomTickCycleReport(3, 30, new[]
        {
            new RoomTickSample(room, 0, 10),
            new RoomTickSample(room, 5, 15),
            new RoomTickSample(room, 20, 30),
        });

        Assert.Equal(2, report.MaxConcurrency);
    }

    [Fact]
    public void Report_AdjacentIntervals_DoNotCountAsConcurrent()
    {
        var room = new RoomKey(new TenantId("t"), new RoomId("r"));
        // Back-to-back (sequential) ticks (start,end): [0,10], [10,20], [20,30] -> never overlap.
        var report = new RoomTickCycleReport(3, 30, new[]
        {
            new RoomTickSample(room, 0, 10),
            new RoomTickSample(room, 10, 20),
            new RoomTickSample(room, 20, 30),
        });

        Assert.Equal(1, report.MaxConcurrency);
    }
}
