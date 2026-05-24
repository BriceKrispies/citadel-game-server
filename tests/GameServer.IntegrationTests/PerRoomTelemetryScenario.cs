using System.Text;
using GameServer.Observability;
using GameServer.Transport;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// Gap #6 - telemetry attribution. The global sink aggregates by metric name with tags
/// folded away, so it can report the mean/max tick time but never which room caused it.
/// This scenario runs one expensive room among many cheap ones, ticks them all, and shows
/// that the global view blends the outlier into a harmless-looking mean (before), while a
/// bounded per-room view names the hot room outright (after).
/// </summary>
public sealed class PerRoomTelemetryScenario
{
    private readonly ITestOutputHelper _output;

    public PerRoomTelemetryScenario(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task GlobalMeanHidesTheHotRoom_PerRoomViewNamesIt()
    {
        const int cheapRooms = 11;
        const double cheapCostMs = 2;
        const double hotCostMs = 40;
        const string hotRoom = "room-hot";

        // One game id per cost tier; the room's cost is fixed by the game it joins.
        var harness = new IntegrationHarness(gameId => new BusyGame(gameId.Value == "hot" ? hotCostMs : cheapCostMs));
        for (var i = 0; i < cheapRooms; i++)
        {
            await harness.JoinAsync("tenant-a", $"room-{i}", "p1", game: "cheap");
        }

        await harness.JoinAsync("tenant-a", hotRoom, "p1", game: "hot");

        var scheduler = ParallelRoomTickScheduler.ForProcessorCount();
        // Warm up first so first-call JIT doesn't masquerade as a hot room, then measure.
        await scheduler.TickCycleAsync(harness.Server.ActiveRooms, harness.TickFn);
        var report = await scheduler.TickCycleAsync(harness.Server.ActiveRooms, harness.TickFn);

        // Before: a by-name sink (tags folded) — the only thing the platform records today.
        var global = new AggregatingTelemetrySink();
        // After: a bounded per-room view.
        var perRoom = new RoomScopedMetrics();
        foreach (var sample in report.Samples)
        {
            global.Measure(TelemetryMetrics.TickDurationMs, sample.ElapsedMs);
            perRoom.Record($"{sample.Room.TenantId.Value}/{sample.Room.RoomId.Value}", sample.ElapsedMs);
        }

        var globalTick = global.Snapshot().Measure(TelemetryMetrics.TickDurationMs);
        var hottest = perRoom.Hottest(3);

        // The blended mean is far below the hot room's cost — the outlier is invisible in it.
        Assert.True(globalTick.Mean < hotCostMs / 2, $"global mean {globalTick.Mean:0.0}ms should hide the {hotCostMs}ms room");
        // The per-room view names the offender.
        Assert.EndsWith(hotRoom, hottest[0].Room);
        Assert.True(hottest[0].MaxMs > globalTick.Mean * 3);

        var dir = ArtifactWriter.Write(
            "06-per-room-telemetry",
            new
            {
                scenario = "per-room-telemetry",
                fixable = true,
                rooms = cheapRooms + 1,
                cheapCostMs,
                hotCostMs,
                before = new
                {
                    view = "global by-name (tags folded)",
                    tickMeanMs = Math.Round(globalTick.Mean, 2),
                    tickMaxMs = Math.Round(globalTick.Max, 2),
                    canAttributeToRoom = false,
                },
                after = new
                {
                    view = "per-room (bounded cardinality)",
                    hottest = hottest.Select(s => new { room = s.Room, meanMs = Math.Round(s.MeanMs, 2), maxMs = Math.Round(s.MaxMs, 2) }),
                },
            },
            BuildMarkdown(cheapRooms + 1, cheapCostMs, hotCostMs, globalTick, hottest));

        _output.WriteLine($"global mean {globalTick.Mean:0.0}ms / max {globalTick.Max:0.0}ms (no room attribution)");
        _output.WriteLine($"hottest room: {hottest[0].Room} at {hottest[0].MaxMs:0.0}ms");
        _output.WriteLine($"artifact: {dir}");
    }

    private static string BuildMarkdown(
        int rooms, double cheapCost, double hotCost, MeasureStats globalTick, IReadOnlyList<RoomStat> hottest)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Gap #6 — Telemetry can't tell you which room is hot");
        sb.AppendLine();
        sb.AppendLine($"{rooms} rooms ticked once: {rooms - 1} at ~{cheapCost:0}ms each, one at ~{hotCost:0}ms.");
        sb.AppendLine();
        sb.AppendLine("## Before — global, by-name (tags folded away)");
        sb.AppendLine();
        sb.AppendLine("| metric | value |");
        sb.AppendLine("|---|---:|");
        sb.AppendLine($"| tick mean | {globalTick.Mean:0.0} ms |");
        sb.AppendLine($"| tick max | {globalTick.Max:0.0} ms |");
        sb.AppendLine($"| which room? | **unknown** |");
        sb.AppendLine();
        sb.AppendLine("The mean is dominated by the cheap rooms; the expensive room is invisible, and even");
        sb.AppendLine("the max can't be attributed to a room. On a busy server this is the difference between");
        sb.AppendLine("\"something is slow\" and \"*this* room is slow\".");
        sb.AppendLine();
        sb.AppendLine("## After — per-room (bounded cardinality, hottest retained)");
        sb.AppendLine();
        sb.AppendLine("| room | mean ms | max ms |");
        sb.AppendLine("|---|---:|---:|");
        foreach (var s in hottest)
        {
            sb.AppendLine($"| {s.Room} | {s.MeanMs:0.0} | {s.MaxMs:0.0} |");
        }

        sb.AppendLine();
        sb.AppendLine("Cardinality is bounded (at most N rooms tracked; the coldest are evicted first), so");
        sb.AppendLine("this stays affordable at thousands of rooms while always retaining the hot ones.");
        return sb.ToString();
    }
}
