using System.Text;
using GameServer.Protocol;
using GameServer.Simulation;
using GameServer.Transport;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// Gap #5 - persistence growth. The in-memory event log is append-only with no
/// compaction, so a long-running room's log grows without bound (one event per applied
/// command, forever) - a steady memory leak for any room that stays up. This scenario
/// drives one room for many ticks and shows the log size before (unbounded) versus after
/// (bounded to a trailing retention window, since older events are already folded into
/// the saved snapshot). Cross-node durable persistence remains out of scope (demonstrate
/// gap #2); this is the in-process growth fix.
/// </summary>
public sealed class EventLogRetentionScenario
{
    private readonly ITestOutputHelper _output;

    public EventLogRetentionScenario(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task EventLogGrowsUnbounded_WithoutRetention_AndIsBounded_WithIt()
    {
        const int ticks = 500;
        const int retention = 64;

        var unbounded = await RunAsync(eventLogRetentionTicks: 0, ticks);
        var bounded = await RunAsync(eventLogRetentionTicks: retention, ticks);

        // Without retention every tick's event is kept forever.
        Assert.Equal(ticks, unbounded);
        // With retention only a trailing window survives, regardless of how long the room runs.
        Assert.Equal(retention, bounded);

        var dir = ArtifactWriter.Write(
            "05-event-log-retention",
            new
            {
                scenario = "event-log-retention",
                fixable = true,
                ticks,
                before = new { retentionTicks = 0, eventsRetained = unbounded },
                after = new { retentionTicks = retention, eventsRetained = bounded },
                note = "cross-node durable persistence is gap #2 (demonstrate-only)",
            },
            BuildMarkdown(ticks, retention, unbounded, bounded));

        _output.WriteLine($"after {ticks} ticks — unbounded log retains {unbounded} events; bounded log retains {bounded}");
        _output.WriteLine($"artifact: {dir}");
    }

    private static async Task<int> RunAsync(int eventLogRetentionTicks, int ticks)
    {
        var harness = new IntegrationHarness(
            _ => new MoveRightGame(),
            lifecycle: RoomLifecycle.Persist,
            eventLogRetentionTicks: eventLogRetentionTicks);

        await harness.JoinAsync("tenant-a", "arena", "p1");
        var key = harness.Key("tenant-a", "arena");
        Assert.True(harness.Router.TryGetRoom(key, out var room));

        // One command per tick → one event per tick: a steadily-running room.
        long sequence = 1;
        for (var t = 0; t < ticks; t++)
        {
            room.TryEnqueue(new PlayerId("p1"), MoveRightGame.MoveRight, sequence++);
            await harness.Server.TickRoom(key);
        }

        return harness.Events.Read(key).Count;
    }

    private static string BuildMarkdown(int ticks, int retention, int unbounded, int bounded)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Gap #5 — Unbounded event-log growth");
        sb.AppendLine();
        sb.AppendLine($"One room, **{ticks} ticks**, one applied command (one event) per tick.");
        sb.AppendLine();
        sb.AppendLine("| retention | events retained after run |");
        sb.AppendLine("|---|---:|");
        sb.AppendLine($"| none (before) | {unbounded} |");
        sb.AppendLine($"| {retention} ticks (after) | {bounded} |");
        sb.AppendLine();
        sb.AppendLine($"Without retention the log keeps every event ever appended ({unbounded} and counting) —");
        sb.AppendLine("a room that stays up leaks memory indefinitely. With retention, each saved snapshot");
        sb.AppendLine("lets the log compact events at or below `tick - retention` (they are already folded");
        sb.AppendLine($"into the snapshot, so recovery never needs them), holding the log flat at ~{retention}");
        sb.AppendLine("events no matter how long the room runs. The host enables a 256-tick window.");
        sb.AppendLine();
        sb.AppendLine("> Durability across process restarts / nodes is a separate concern (the store is still");
        sb.AppendLine("> in-memory) — that's part of gap #2 and is demonstrate-only here.");
        return sb.ToString();
    }
}
