using GameServer.Transport;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// Gap #15 (graceful degradation) — the observe→act loop is open. The server measures
/// <c>missed_ticks</c> and <c>tick_duration_ms</c>, but nothing acts on them: admission is binary
/// (admit until a hard ceiling, then hard-reject) and there is no mechanism to trade optional work
/// for tick headroom under sustained overload, so the server fails at a cliff instead of degrading.
/// This scenario drives a real overload (many CPU-heavy rooms so tick cycles blow the budget),
/// measures the genuine missed-tick rate and tick p95, and asserts a controller escalates the
/// service-degradation level above Normal. It FAILS today via the unimplemented
/// <see cref="IDegradationController"/> seam, and turns green once the ladder exists and is wired
/// to shed optional load (spectator snapshot frequency, telemetry, then new rooms/connections).
/// </summary>
/// <remarks>Integration scenario: burns real CPU (BusyGame) to produce honest overload signals.</remarks>
public sealed class DegradationLadderScenario
{
    private readonly ITestOutputHelper _output;

    public DegradationLadderScenario(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task SustainedOverload_EscalatesTheDegradationLevel()
    {
        const double tickBudgetMs = 50.0;

        // Enough heavy rooms that a full tick cycle cannot fit the budget.
        var harness = new IntegrationHarness(_ => new BusyGame(costMs: 20));
        for (var i = 0; i < 8; i++)
        {
            await harness.JoinAsync("tenant-a", $"room-{i}", "p1");
        }

        var scheduler = ParallelRoomTickScheduler.ForProcessorCount();
        await scheduler.TickCycleAsync(harness.Server.ActiveRooms, harness.TickFn); // warm up

        var cycleTimes = new List<double>();
        for (var c = 0; c < 6; c++)
        {
            var report = await scheduler.TickCycleAsync(harness.Server.ActiveRooms, harness.TickFn);
            cycleTimes.Add(report.TotalElapsedMs);
        }

        var missedTickRate = cycleTimes.Count(t => t > tickBudgetMs) / (double)cycleTimes.Count;
        var tickP95Ms = Percentile(cycleTimes, 95);

        _output.WriteLine($"under load: missedTickRate={missedTickRate:0.00}, tickP95={tickP95Ms:0.0}ms (budget {tickBudgetMs}ms)");

        var controller = new LadderDegradationController(tickBudgetMs);
        var level = controller.Observe(missedTickRate, tickP95Ms);

        Assert.True(
            level > DegradationLevel.Normal,
            $"Gap #15: under sustained overload (missedTickRate {missedTickRate:0.00}, p95 {tickP95Ms:0.0}ms vs {tickBudgetMs}ms budget) " +
            "the server stayed at full service. A degradation controller must escalate the level so the data plane sheds optional " +
            "work (spectator snapshot frequency, telemetry, then new rooms/connections) before authoritative correctness is at risk.");
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Count - 1)];
    }
}
