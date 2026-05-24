using System.Text;
using GameServer.Protocol;
using GameServer.Replication;
using GameServer.Simulation;
using GameServer.Transport;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// Admin "drop in" - read-only observation of a live room. Drives a real delta-mode room
/// with two sessions where neither client acks, then takes the same observation the admin
/// endpoint serves and shows it reveals the authoritative entity state AND each session's
/// lag (unacknowledged-snapshot backlog) - exactly what you need to debug "what is this
/// player seeing / why are they behind", without touching room state.
/// </summary>
public sealed class AdminObservationScenario
{
    private readonly ITestOutputHelper _output;

    public AdminObservationScenario(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task ObservingARoom_RevealsAuthoritativeStateAndPerSessionLag()
    {
        const int ticks = 5;

        // Delta mode makes per-viewer lag observable (full mode keeps no per-viewer baseline).
        var harness = new IntegrationHarness(
            _ => new MoveRightGame(),
            policy: _ => ReplicationPolicy.Default with { SnapshotMode = SnapshotMode.Delta },
            lifecycle: RoomLifecycle.Persist);

        await harness.JoinAsync("tenant-a", "arena", "p1");
        await harness.JoinAsync("tenant-a", "arena", "p2");
        var key = harness.Key("tenant-a", "arena");
        Assert.True(harness.Router.TryGetRoom(key, out var room));

        // p1 advances; nobody acks, so the server keeps resending and the backlog grows.
        long sequence = 1;
        for (var t = 0; t < ticks; t++)
        {
            room.TryEnqueue(new PlayerId("p1"), MoveRightGame.MoveRight, sequence++);
            await harness.Server.TickRoom(key);
        }

        Assert.True(harness.Server.TryObserveRoom(key, out var observation));

        var p1 = observation.Entities.Single(e => e.EntityId == "p1");
        Assert.Equal(5, p1.X); // five moves → relevance key X = 5, no payload decode needed
        Assert.Contains(observation.Entities, e => e.EntityId == "p2");

        Assert.Equal(2, observation.Viewers.Count);
        Assert.All(observation.Viewers, v => Assert.Equal(ticks, v.PendingSnapshots)); // both are `ticks` behind

        var dir = ArtifactWriter.Write(
            "07-admin-observation",
            new
            {
                scenario = "admin-observation",
                readOnly = true,
                observed = new
                {
                    observation.TenantId,
                    observation.RoomId,
                    observation.Tick,
                    entities = observation.Entities.Select(e => new { e.EntityId, e.X, e.Y, e.Version }),
                    viewers = observation.Viewers.Select(v => new { v.ConnectionId, v.PlayerId, v.PendingSnapshots }),
                },
            },
            BuildMarkdown(observation));

        _output.WriteLine($"observed {observation.RoomId} @tick {observation.Tick}: {observation.Entities.Count} entities, {observation.Viewers.Count} viewers");
        _output.WriteLine($"artifact: {dir}");
    }

    private static string BuildMarkdown(RoomObservation observation)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Admin drop-in — observing a live session");
        sb.AppendLine();
        sb.AppendLine($"Room **{observation.TenantId}/{observation.RoomId}** at authoritative tick **{observation.Tick}**.");
        sb.AppendLine("This is the read-only view the admin endpoint serves (and the console renders live).");
        sb.AppendLine();
        sb.AppendLine("## Authoritative entities (plotted by relevance key — game-agnostic)");
        sb.AppendLine();
        sb.AppendLine("| entity | x | y | version |");
        sb.AppendLine("|---|---:|---:|---:|");
        foreach (var e in observation.Entities)
        {
            sb.AppendLine($"| {e.EntityId} | {e.X:0} | {e.Y:0} | {e.Version} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Sessions in the room (with lag)");
        sb.AppendLine();
        sb.AppendLine("| player | unacked snapshots (lag) |");
        sb.AppendLine("|---|---:|");
        foreach (var v in observation.Viewers)
        {
            sb.AppendLine($"| {v.PlayerId ?? "—"} | {v.PendingSnapshots} |");
        }

        sb.AppendLine();
        sb.AppendLine("Both sessions are several snapshots behind because neither is acknowledging — the");
        sb.AppendLine("signature of a stalled/dropped client. An operator can see this per session, live,");
        sb.AppendLine("without attaching a debugger or touching the room. The endpoints are authenticated");
        sb.AppendLine("(control-plane key), tenant-authorized, and audited; the observation never mutates state.");
        return sb.ToString();
    }
}
