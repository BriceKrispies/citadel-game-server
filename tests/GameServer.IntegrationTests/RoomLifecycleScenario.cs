using System.Text;
using GameServer.Simulation;
using GameServer.Transport;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// Gap #3 - room/subscriber lifecycle. Originally a room was created on first join and
/// never torn down, and a connection was pruned from a room only when a send to it threw
/// - so a clean disconnect left the room and its dead subscriber behind forever. This
/// scenario churns clients through many distinct rooms (each joins once and leaves) and
/// shows the leak under the legacy <see cref="RoomLifecycle.Persist"/> policy versus a
/// clean teardown under the new <see cref="RoomLifecycle.Reap"/> policy (the host default).
/// </summary>
public sealed class RoomLifecycleScenario
{
    private readonly ITestOutputHelper _output;

    public RoomLifecycleScenario(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task EmptyRoomsLeakUnderPersist_AndAreReapedUnderReap()
    {
        const int distinctRooms = 50;

        var leaked = await ChurnAsync(RoomLifecycle.Persist, distinctRooms);
        var reaped = await ChurnAsync(RoomLifecycle.Reap, distinctRooms);

        // Before: every room ever joined lingers, each with its dead subscriber.
        Assert.Equal(distinctRooms, leaked.RoomsAfter);
        Assert.Equal(distinctRooms, leaked.SubscribedRoomsAfter);

        // After: nothing is left once each room empties out.
        Assert.Equal(0, reaped.RoomsAfter);
        Assert.Equal(0, reaped.SubscribedRoomsAfter);

        var dir = ArtifactWriter.Write(
            "03-room-lifecycle",
            new
            {
                scenario = "room-lifecycle",
                fixable = true,
                clientsChurned = distinctRooms,
                before = new { policy = "Persist", roomsLeaked = leaked.RoomsAfter, subscribedRooms = leaked.SubscribedRoomsAfter },
                after = new { policy = "Reap", roomsLeaked = reaped.RoomsAfter, subscribedRooms = reaped.SubscribedRoomsAfter },
            },
            BuildMarkdown(distinctRooms, leaked, reaped));

        _output.WriteLine($"after churning {distinctRooms} rooms — Persist leaks {leaked.RoomsAfter} rooms; Reap leaves {reaped.RoomsAfter}");
        _output.WriteLine($"artifact: {dir}");
    }

    private static async Task<ChurnResult> ChurnAsync(RoomLifecycle lifecycle, int distinctRooms)
    {
        var harness = new IntegrationHarness(_ => new MoveRightGame(), lifecycle: lifecycle);

        // Each client joins a distinct room and immediately leaves (clean disconnect).
        for (var i = 0; i < distinctRooms; i++)
        {
            await harness.JoinAsync("tenant-a", $"room-{i}", "p1");
        }

        return new ChurnResult(harness.Router.RoomCount, harness.Server.ActiveRooms.Count);
    }

    private sealed record ChurnResult(int RoomsAfter, int SubscribedRoomsAfter);

    private static string BuildMarkdown(int churned, ChurnResult before, ChurnResult after)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Gap #3 — Empty rooms (and dead subscribers) leak");
        sb.AppendLine();
        sb.AppendLine($"Churned **{churned}** clients, each joining a distinct room then disconnecting cleanly.");
        sb.AppendLine("A leak shows up as routed rooms (and subscriber sets) that never fall back to zero.");
        sb.AppendLine();
        sb.AppendLine("| policy | routed rooms after | rooms with subscribers after |");
        sb.AppendLine("|---|---:|---:|");
        sb.AppendLine($"| Persist (before) | {before.RoomsAfter} | {before.SubscribedRoomsAfter} |");
        sb.AppendLine($"| Reap (after, host default) | {after.RoomsAfter} | {after.SubscribedRoomsAfter} |");
        sb.AppendLine();
        sb.AppendLine("Under `Persist` the count grows one-for-one with every distinct room ever touched —");
        sb.AppendLine("unbounded over the life of the process. Under `Reap` a room is torn down (router");
        sb.AppendLine("entry, subscriber set, and replicator all dropped) when its last connection leaves,");
        sb.AppendLine("so memory tracks *live* rooms. The fix also always prunes a closed connection from");
        sb.AppendLine("its room, rather than only when a send happens to fail.");
        sb.AppendLine();
        sb.AppendLine("> Note: teardown is serialized against join via a per-room lock so a last-leaver reap");
        sb.AppendLine("> cannot race a concurrent join. Cross-node room ownership remains gap #2.");
        return sb.ToString();
    }
}
