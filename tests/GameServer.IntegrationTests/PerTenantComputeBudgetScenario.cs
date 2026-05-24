using GameServer.Protocol;
using GameServer.Tenancy;
using GameServer.Transport;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// Gap #13 (tenant fairness) — no per-tenant compute fair-share. Rooms are ticked from a shared
/// worker pool with no notion of tenant, so a tenant running many expensive rooms (heavy game
/// logic) monopolizes the tick workers and delays every other tenant's ticks — a CPU noisy
/// neighbor. This scenario builds a real imbalance (tenant-a: several CPU-heavy rooms; tenant-b: a
/// single cheap room), runs an actual tick cycle to measure genuine per-room costs, then charges
/// those costs to a per-tenant compute budget (greedy: the heavy tenant first) and asserts the
/// cheap tenant still gets ticked. It FAILS today via the unimplemented
/// <see cref="ITenantComputeBudget"/> seam, and turns green once the scheduler budgets tick time
/// per tenant so an over-budget tenant yields instead of starving the others.
/// </summary>
/// <remarks>Integration scenario: burns real CPU (BusyGame) to produce honest tick costs.</remarks>
public sealed class PerTenantComputeBudgetScenario
{
    private readonly ITestOutputHelper _output;

    public PerTenantComputeBudgetScenario(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task ExpensiveTenant_DoesNotStarve_ACheapTenantsTick()
    {
        const string heavyGame = "heavy";
        const string lightGame = "light";

        var harness = new IntegrationHarness(
            gameId => new BusyGame(gameId.Value == heavyGame ? 25 : 1),
            tenants: new[] { "tenant-a", "tenant-b" });

        // tenant-a: four CPU-heavy rooms. tenant-b: one cheap room.
        for (var i = 0; i < 4; i++)
        {
            await harness.JoinAsync("tenant-a", $"a-room-{i}", "p1", game: heavyGame);
        }

        await harness.JoinAsync("tenant-b", "b-room-0", "p1", game: lightGame);

        var scheduler = ParallelRoomTickScheduler.ForProcessorCount();
        await scheduler.TickCycleAsync(harness.Server.ActiveRooms, harness.TickFn); // warm up the JIT
        var report = await scheduler.TickCycleAsync(harness.Server.ActiveRooms, harness.TickFn);

        // A cycle budget far smaller than the heavy tenant's total cost: a fair scheduler must
        // still reserve a cheap tenant's slice rather than letting the heavy tenant consume it all.
        var budget = new FairTenantComputeBudget(TimeSpan.FromMilliseconds(20));
        budget.BeginCycle();

        // Charge the heavy tenant first (worst case: a greedy scheduler reaches it first).
        foreach (var sample in report.Samples.Where(s => s.Room.TenantId.Value == "tenant-a"))
        {
            budget.TryConsume(sample.Room.TenantId, TimeSpan.FromMilliseconds(sample.ElapsedMs));
        }

        var cheap = report.Samples.Single(s => s.Room.TenantId.Value == "tenant-b");
        var cheapTenantGotTicked = budget.TryConsume(cheap.Room.TenantId, TimeSpan.FromMilliseconds(cheap.ElapsedMs));

        _output.WriteLine($"tenant-b ticked under budget pressure: {cheapTenantGotTicked}");

        Assert.True(
            cheapTenantGotTicked,
            "Gap #13: tenant-b's cheap room was starved of tick budget by tenant-a's expensive rooms. " +
            "The room tick scheduler must reserve a per-tenant compute fair-share (ITenantComputeBudget) so an " +
            "over-budget tenant yields instead of monopolizing the tick workers.");
    }
}
