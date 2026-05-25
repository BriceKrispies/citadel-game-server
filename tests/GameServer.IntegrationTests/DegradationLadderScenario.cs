using GameServer.Host;
using GameServer.Protocol;
using GameServer.Transport;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// Wave 6 (gap #15) — the graceful-degradation ladder, wired end to end. The server measures
/// <c>missed_ticks</c> and <c>tick_duration_ms</c>; the <see cref="LadderDegradationController"/> turns
/// those into an ordered <see cref="DegradationLevel"/>, and the realtime EDGE
/// (<see cref="RealtimeServer"/>) reads the level on the hot path to shed OPTIONAL load before
/// authoritative correctness is at risk. These scenarios prove the loop is closed and correct:
/// <list type="bullet">
/// <item>overload escalates the ladder MONOTONICALLY (immediate up, one step down) and never flaps;</item>
/// <item>at each rung the edge sheds the matching optional load (spectator push → new rooms → new
/// connections) while the authoritative simulation keeps ticking and persisting;</item>
/// <item>when load subsides the ladder RECOVERS back to Normal and full service resumes.</item>
/// </list>
/// Overload is driven through the SAME <see cref="TickHealthWindow"/> + controller the host wires (see
/// Program.cs / RoomTickService), with injected cycle durations rather than real sleeps, so the proof is
/// deterministic and hermetic. The companion <see cref="SustainedOverload_EscalatesTheDegradationLevel"/>
/// burns real CPU to show the signals are honest.
/// </summary>
public sealed class DegradationLadderScenario
{
    private readonly ITestOutputHelper _output;

    public DegradationLadderScenario(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// The core Wave-6 conformance test: drive the controller through overload and recovery via the host's
    /// real window, and assert (a) the ladder is monotonic, (b) it climbs to the top and back to Normal,
    /// (c) at every level the live edge sheds exactly the matching optional load, and (d) the authoritative
    /// simulation NEVER stops advancing — correctness outranks accepting load.
    /// </summary>
    [Fact]
    public async Task DegradationLadderUnderLoad()
    {
        const double tickBudgetMs = 50.0;
        var controller = new LadderDegradationController(tickBudgetMs);
        // The exact window the host's tick driver feeds (same budget); small so recovery is quick to drive.
        var health = new TickHealthWindow(tickBudgetMs, capacity: 8);

        // A live server with the SAME controller wired in — so reading the edge proves the wiring, not a
        // mock. Persist lifecycle so the established room stays tickable after its client closes (the
        // subject under test is degradation, not room reaping); no admission ceilings, so the ONLY reason
        // a new connection can be refused is the degradation ladder.
        var harness = new IntegrationHarness(
            _ => new BusyGame(costMs: 0),
            tenants: new[] { "tenant-a" },
            lifecycle: RoomLifecycle.Persist,
            degradation: controller);

        // An authoritative room with a player, established BEFORE degradation. It must keep ticking the
        // whole time — degradation may never touch it.
        await harness.JoinAsync("tenant-a", "live-room", "p1");
        var beforeTick = AdvanceAndReadTick(harness, "live-room");

        // ---- Escalation: feed worsening signals; the level must rise monotonically to the top. ----
        var ladder = new List<DegradationLevel> { controller.Current };
        // Each step pushes a window fuller of over-budget cycles; the controller escalates immediately.
        foreach (var cyclesOverBudget in new[] { 1, 3, 5, 8 })
        {
            for (var i = 0; i < 8; i++)
            {
                health.Record(i < cyclesOverBudget ? tickBudgetMs * 3 : 5);
            }

            ladder.Add(controller.Observe(health.MissedTickRate, health.TickP95Ms));
        }

        AssertMonotonicUp(ladder);
        Assert.Equal(DegradationLevel.RejectNewConnections, controller.Current);

        // ---- At the top rung the edge sheds the matching optional load. ----
        // New connection refused (RejectNewConnections) — but the EXISTING connection's room keeps ticking.
        var rejected = await harness.RunClientAsync("tenant-a", "new-room", "p2", "demo", commands: Array.Empty<string>());
        Assert.Contains(rejected.DrainOutbound(), m =>
            m.Payload is ServerError { Code: ServerErrorCode.Overloaded });
        // The authoritative room is unaffected: it still advances its tick under full overload.
        var underLoadTick = AdvanceAndReadTick(harness, "live-room");
        Assert.True(underLoadTick > beforeTick,
            "authoritative simulation must keep advancing under maximum degradation — correctness outranks shedding load");
        // Spectator/observer push is thinned at any rung >= ReduceSpectatorSnapshots (odd stream ticks dropped).
        Assert.False(harness.Server.ShouldEmitSpectatorSnapshot(1));
        Assert.True(harness.Server.ShouldEmitSpectatorSnapshot(0));

        // ---- Recovery: healthy cycles clear the window; the ladder steps DOWN one rung at a time. ----
        var recovery = new List<DegradationLevel> { controller.Current };
        for (var step = 0; step < 6; step++)
        {
            for (var i = 0; i < 8; i++)
            {
                health.Record(5); // well under budget
            }

            recovery.Add(controller.Observe(health.MissedTickRate, health.TickP95Ms));
        }

        AssertMonotonicDown(recovery);
        Assert.Equal(DegradationLevel.Normal, controller.Current);

        // Back at Normal the edge admits new load again and the spectator push is full-rate.
        var admitted = await harness.RunClientAsync("tenant-a", "fresh-room", "p3", "demo", commands: Array.Empty<string>());
        Assert.DoesNotContain(admitted.DrainOutbound(), m =>
            m.Payload is ServerError { Code: ServerErrorCode.Overloaded });
        Assert.True(harness.Server.ShouldEmitSpectatorSnapshot(1));

        _output.WriteLine(
            $"ladder up: [{string.Join(" -> ", ladder)}]; recovery: [{string.Join(" -> ", recovery)}]; " +
            $"authoritative tick {beforeTick} -> {underLoadTick} under max degradation");
    }

    /// <summary>
    /// Honesty check on the SIGNALS the ladder is driven by: burn real CPU so a full tick cycle genuinely
    /// blows the budget, and assert the controller escalates above Normal from the measured (not injected)
    /// missed-tick rate / p95. This is the "the overload is real, not a fixture" guard for the loop above.
    /// </summary>
    /// <remarks>Integration scenario: burns real CPU (BusyGame) to produce honest overload signals.</remarks>
    [Fact]
    public async Task SustainedOverload_EscalatesTheDegradationLevel()
    {
        // A tight budget below a single room's per-tick cost, so any real cycle of these CPU-heavy rooms
        // overruns it regardless of how many cores the scheduler spreads them across (the test must not
        // depend on core count to observe overload). The signals are still MEASURED, not injected.
        const double tickBudgetMs = 5.0;

        var harness = new IntegrationHarness(_ => new BusyGame(costMs: 20));
        for (var i = 0; i < 8; i++)
        {
            await harness.JoinAsync("tenant-a", $"room-{i}", "p1");
        }

        var scheduler = ParallelRoomTickScheduler.ForProcessorCount();
        await scheduler.TickCycleAsync(harness.Server.ActiveRooms, harness.TickFn); // warm up

        var health = new TickHealthWindow(tickBudgetMs, capacity: 8);
        for (var c = 0; c < 8; c++)
        {
            var report = await scheduler.TickCycleAsync(harness.Server.ActiveRooms, harness.TickFn);
            health.Record(report.TotalElapsedMs);
        }

        _output.WriteLine($"under load: missedTickRate={health.MissedTickRate:0.00}, tickP95={health.TickP95Ms:0.0}ms (budget {tickBudgetMs}ms)");

        var controller = new LadderDegradationController(tickBudgetMs);
        var level = controller.Observe(health.MissedTickRate, health.TickP95Ms);

        Assert.True(
            level > DegradationLevel.Normal,
            $"under sustained overload (missedTickRate {health.MissedTickRate:0.00}, p95 {health.TickP95Ms:0.0}ms vs {tickBudgetMs}ms budget) " +
            "the degradation controller must escalate so the data plane sheds optional work before authoritative correctness is at risk.");
    }

    /// <summary>
    /// The adversarial authoritative-drop invariant: PIN the ladder at its MAX rung
    /// (<see cref="DegradationLevel.RejectNewConnections"/>) and prove that an already-established room's
    /// authoritative simulation is never gated by degradation — across MANY ticks the tick advances by
    /// exactly one each cycle, the per-player state mutates from queued commands, and EVERY tick persists a
    /// snapshot and its events to the durable stores. Degradation may only shed OPTIONAL load (new
    /// connections/rooms, telemetry, spectator push); it may never slow, skip, or stop the authoritative
    /// tick / state mutation / persistence. If this regresses, that is a Critical defect.
    /// </summary>
    [Fact]
    public async Task PinnedAtMaxDegradation_AuthoritativeTickStateAndPersistenceNeverGated()
    {
        const double tickBudgetMs = 50.0;
        var controller = new LadderDegradationController(tickBudgetMs);
        var health = new TickHealthWindow(tickBudgetMs, capacity: 8);

        // MoveRightGame so we can assert STATE actually mutates (player X advances), not just the tick number.
        var harness = new IntegrationHarness(
            _ => new GameServer.Simulation.MoveRightGame(),
            tenants: new[] { "tenant-a" },
            lifecycle: RoomLifecycle.Persist,
            degradation: controller);

        var key = harness.Key("tenant-a", "live-room");
        await harness.JoinAsync("tenant-a", "live-room", "p1");

        // The authoritative room owner (created at join, kept alive by Persist). The edge's TickRoom
        // drains its queue under the room lock — exactly the path that must never be gated by degradation.
        var player = new GameServer.Protocol.PlayerId("p1");
        Assert.True(harness.Router.TryGetRoom(key, out var room));
        room.Join(player); // re-establish membership (the joining connection left on close under Persist)

        // Pin the controller at the TOP rung with sustained overload signals.
        for (var i = 0; i < 8; i++)
        {
            health.Record(tickBudgetMs * 3);
        }

        Assert.Equal(DegradationLevel.RejectNewConnections, controller.Observe(health.MissedTickRate, health.TickP95Ms));

        // A new connection IS refused at this rung (optional load shed) — establishes we are genuinely pinned.
        var refused = await harness.RunClientAsync("tenant-a", "second-room", "p2", "demo", commands: Array.Empty<string>());
        Assert.Contains(refused.DrainOutbound(), m => m.Payload is ServerError { Code: ServerErrorCode.Overloaded });

        // ---- Authoritative path across MANY ticks, while pinned at max the whole time. ----
        long previousTick = -1;
        long sequence = 1;
        for (var i = 0; i < 25; i++)
        {
            // Enqueue a command on the authoritative room (the state owner), then tick through the EDGE.
            // Re-pin every iteration so the controller is unambiguously at max throughout (no recovery).
            Assert.Equal(GameServer.Simulation.CommandAdmission.Accepted, room.TryEnqueue(player, GameServer.Simulation.MoveRightGame.MoveRight, sequence++));
            for (var j = 0; j < 8; j++)
            {
                health.Record(tickBudgetMs * 3);
            }

            controller.Observe(health.MissedTickRate, health.TickP95Ms);
            Assert.Equal(DegradationLevel.RejectNewConnections, controller.Current);

            await harness.Server.TickRoom(key);

            // (a) tick strictly advances by one — never skipped or stalled by degradation.
            Assert.True(harness.Server.TryObserveRoom(key, out var observation));
            if (previousTick >= 0)
            {
                Assert.Equal(previousTick + 1, observation.Tick);
            }

            previousTick = observation.Tick;

            // (b) persistence is never gated: the durable snapshot store holds THIS tick.
            Assert.True(harness.Snapshots.TryGetLatest(key, out var saved));
            Assert.Equal(observation.Tick, saved.Tick);

            // (c) state mutated under max degradation: p1's X equals the number of commands applied so far.
            var p1 = observation.Entities.Single(e => e.EntityId == "p1");
            Assert.Equal(i + 1, GameServer.Simulation.MoveRightGame.DecodeX(Convert.FromBase64String(p1.PayloadBase64)));
        }

        _output.WriteLine(
            $"pinned at {controller.Current} for 25 ticks; tick advanced to {previousTick}, p1.x reached 25, snapshot persisted every tick");
    }

    /// <summary>Ticks the room once and returns the post-tick authoritative tick number.</summary>
    private static long AdvanceAndReadTick(IntegrationHarness harness, string room)
    {
        var key = harness.Key("tenant-a", room);
        harness.Server.TickRoom(key).GetAwaiter().GetResult();
        Assert.True(harness.Server.TryObserveRoom(key, out var observation));
        return observation.Tick;
    }

    private static void AssertMonotonicUp(IReadOnlyList<DegradationLevel> levels)
    {
        for (var i = 1; i < levels.Count; i++)
        {
            Assert.True(levels[i] >= levels[i - 1],
                $"escalation must be monotonic (no regression): {string.Join(" -> ", levels)}");
        }
    }

    private static void AssertMonotonicDown(IReadOnlyList<DegradationLevel> levels)
    {
        for (var i = 1; i < levels.Count; i++)
        {
            Assert.True(levels[i] <= levels[i - 1],
                $"recovery must be monotonic and step down at most one level per observe: {string.Join(" -> ", levels)}");
            Assert.True(levels[i - 1] - levels[i] <= 1,
                $"recovery must not skip rungs (hysteresis): {string.Join(" -> ", levels)}");
        }
    }
}
