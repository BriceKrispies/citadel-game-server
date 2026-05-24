using System.Text;
using GameServer.Transport;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// Gap #1 - the authoritative tick loop. Demonstrates that ticking rooms serially makes
/// one room wait behind every room before it (head-of-line blocking) and uses a single
/// core, whereas the parallel scheduler ticks independent rooms concurrently so a cycle
/// costs about as much as the slowest single room. Drives the real <see cref="RealtimeServer"/>
/// over many rooms whose per-tick cost is a fixed slug of CPU, and writes a before/after
/// artifact (timings + ASCII timelines).
/// </summary>
public sealed class ParallelTickingScenario
{
    private readonly ITestOutputHelper _output;

    public ParallelTickingScenario(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task SerialTickingHeadOfLineBlocks_ParallelTickingDoesNot()
    {
        const int roomCount = 8;
        const double costMsPerRoom = 20;

        var harness = new IntegrationHarness(_ => new BusyGame(costMsPerRoom));
        for (var i = 0; i < roomCount; i++)
        {
            await harness.JoinAsync("tenant-a", $"room-{i}", $"p{i}");
        }

        var rooms = harness.Server.ActiveRooms;
        Assert.Equal(roomCount, rooms.Count);

        // Before: the original behavior. After: the fix.
        var sequential = await new SequentialRoomTickScheduler().TickCycleAsync(rooms, harness.TickFn);
        var parallel = await ParallelRoomTickScheduler.ForProcessorCount().TickCycleAsync(rooms, harness.TickFn);

        // Serial ticking is strictly one-at-a-time, regardless of machine.
        Assert.Equal(1, sequential.MaxConcurrency);

        // The speedup is only assertable where there is more than one core to use.
        if (Environment.ProcessorCount > 1)
        {
            Assert.True(parallel.MaxConcurrency >= 2,
                $"expected concurrent ticks on a {Environment.ProcessorCount}-core machine, saw {parallel.MaxConcurrency}");
            Assert.True(parallel.TotalElapsedMs < sequential.TotalElapsedMs,
                $"parallel cycle ({parallel.TotalElapsedMs:0.0}ms) was not faster than serial ({sequential.TotalElapsedMs:0.0}ms)");
        }

        var speedup = parallel.TotalElapsedMs > 0 ? sequential.TotalElapsedMs / parallel.TotalElapsedMs : 0;
        var dir = ArtifactWriter.Write(
            "01-parallel-ticking",
            new
            {
                scenario = "parallel-ticking",
                machine = new { processorCount = Environment.ProcessorCount },
                roomCount,
                costMsPerRoom,
                sequential = Summarize(sequential),
                parallel = Summarize(parallel),
                speedup = Math.Round(speedup, 2),
            },
            BuildMarkdown(roomCount, costMsPerRoom, sequential, parallel, speedup));

        _output.WriteLine($"serial cycle:   {sequential.TotalElapsedMs:0.0} ms (max concurrency {sequential.MaxConcurrency})");
        _output.WriteLine($"parallel cycle: {parallel.TotalElapsedMs:0.0} ms (max concurrency {parallel.MaxConcurrency})");
        _output.WriteLine($"speedup: {speedup:0.0}x on {Environment.ProcessorCount} cores");
        _output.WriteLine($"artifact: {dir}");
    }

    private static object Summarize(RoomTickCycleReport report) => new
    {
        totalElapsedMs = Math.Round(report.TotalElapsedMs, 2),
        maxConcurrency = report.MaxConcurrency,
        samples = report.Samples
            .OrderBy(s => s.StartOffsetMs)
            .Select(s => new
            {
                room = s.Room.RoomId.Value,
                startOffsetMs = Math.Round(s.StartOffsetMs, 2),
                elapsedMs = Math.Round(s.ElapsedMs, 2),
            }),
    };

    private static string BuildMarkdown(
        int roomCount, double costMs, RoomTickCycleReport sequential, RoomTickCycleReport parallel, double speedup)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Gap #1 — Parallel ticking removes head-of-line blocking");
        sb.AppendLine();
        sb.AppendLine($"- machine: **{Environment.ProcessorCount} logical cores**");
        sb.AppendLine($"- workload: **{roomCount} rooms**, each tick costs **{costMs:0} ms** of CPU");
        sb.AppendLine();
        sb.AppendLine("| scheduler | total cycle (ms) | max concurrency | speedup |");
        sb.AppendLine("|---|---:|---:|---:|");
        sb.AppendLine($"| sequential (before) | {sequential.TotalElapsedMs:0.0} | {sequential.MaxConcurrency} | 1.00x |");
        sb.AppendLine($"| parallel (after) | {parallel.TotalElapsedMs:0.0} | {parallel.MaxConcurrency} | {speedup:0.00}x |");
        sb.AppendLine();
        sb.AppendLine("A serial cycle costs ~`rooms × per-room cost` because every room waits for the one");
        sb.AppendLine("before it; the parallel cycle approaches `ceil(rooms / cores) × per-room cost`.");
        sb.AppendLine();
        sb.AppendLine("## Sequential timeline — one room at a time (head-of-line blocking)");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.Append(Timeline(sequential));
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Parallel timeline — independent rooms tick concurrently");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.Append(Timeline(parallel));
        sb.AppendLine("```");
        return sb.ToString();
    }

    // An ASCII Gantt of the cycle: each row is a room, the bar spans its tick interval
    // scaled to the whole cycle. Serial shows a staircase; parallel shows aligned bars.
    private static string Timeline(RoomTickCycleReport report, int width = 56)
    {
        var span = Math.Max(report.TotalElapsedMs, 0.001);
        var sb = new StringBuilder();
        foreach (var s in report.Samples.OrderBy(x => x.StartOffsetMs))
        {
            var start = (int)(s.StartOffsetMs / span * width);
            var len = Math.Max(1, (int)(s.ElapsedMs / span * width));
            start = Math.Min(start, width - 1);
            len = Math.Min(len, width - start);
            sb.Append(s.Room.RoomId.Value.PadRight(8));
            sb.Append('|');
            sb.Append(new string('.', start));
            sb.Append(new string('#', len));
            sb.Append(new string('.', Math.Max(0, width - start - len)));
            sb.Append('|');
            sb.Append($" {s.ElapsedMs:0.0}ms");
            sb.Append('\n');
        }

        return sb.ToString();
    }
}
