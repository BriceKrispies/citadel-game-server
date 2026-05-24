using System.Text;
using GameServer.Routing;
using GameServer.Simulation;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// Gap #2 - horizontal scale / room ownership. The platform has no concept of which node
/// owns a room: a <c>RoomKey</c> is created lazily on first join inside whatever process
/// receives the connection. This scenario stands up two independent server "nodes" (two
/// full in-process stacks, exactly what two processes behind a load balancer would be)
/// and shows the same tenant+room resolving to two separate authoritative rooms whose
/// state diverges - split brain. There is no fix in a single process; the artifact
/// documents what's missing (a room-&gt;node ownership registry + sticky routing).
/// </summary>
public sealed class MultiNodeOwnershipScenario
{
    private readonly ITestOutputHelper _output;

    public MultiNodeOwnershipScenario(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task SameRoomOnTwoNodes_DivergesWithNoSingleOwner()
    {
        const string tenant = "tenant-a";
        const string room = "arena";
        const string player = "p1";

        // Two independent nodes. Nothing is shared between them - no ownership registry,
        // no shared store, no routing affinity.
        var nodeA = new IntegrationHarness(_ => new MoveRightGame());
        var nodeB = new IntegrationHarness(_ => new MoveRightGame());
        var key = new RoomKey(new GameServer.Protocol.TenantId(tenant), new GameServer.Protocol.RoomId(room));

        // The same player, with the same identity, "lands on" different nodes (as a reconnect
        // or a load balancer rehash would do) and acts. Each node accepts the join — neither
        // checks whether another node already owns this room.
        await nodeA.RunClientAsync(tenant, room, player, "demo", Repeat("MoveRight", 5));
        await nodeB.RunClientAsync(tenant, room, player, "demo", Repeat("MoveRight", 2));

        await nodeA.Server.TickRoom(key);
        await nodeB.Server.TickRoom(key);

        var xOnA = ReadX(nodeA, key, player);
        var xOnB = ReadX(nodeB, key, player);

        // Same tenant, same room id, same player — two different authoritative truths.
        Assert.Equal(5, xOnA);
        Assert.Equal(2, xOnB);
        Assert.NotEqual(xOnA, xOnB);

        var dir = ArtifactWriter.Write(
            "02-multi-node-ownership",
            new
            {
                scenario = "multi-node-ownership",
                fixable = false,
                roomKey = new { tenant, room },
                player,
                authoritativeStateByNode = new[]
                {
                    new { node = "A", playerX = xOnA },
                    new { node = "B", playerX = xOnB },
                },
                diverged = xOnA != xOnB,
                missing = new[]
                {
                    "room->node ownership registry (exactly one owner per RoomKey)",
                    "sticky routing / node affinity so a client always reaches the owning node",
                    "ownership handoff + fencing on node failure",
                },
            },
            BuildMarkdown(tenant, room, player, xOnA, xOnB));

        _output.WriteLine($"node A sees p1.x={xOnA}; node B sees p1.x={xOnB} for the SAME room '{tenant}/{room}'");
        _output.WriteLine($"artifact: {dir}");
    }

    private static int ReadX(IntegrationHarness node, RoomKey key, string player)
    {
        Assert.True(node.Router.TryGetRoom(key, out var room));
        var entity = room.Project().Single(e => e.Id.Value == player);
        return MoveRightGame.DecodeX(entity.Payload);
    }

    private static IReadOnlyList<string> Repeat(string command, int times) =>
        Enumerable.Repeat(command, times).ToArray();

    private static string BuildMarkdown(string tenant, string room, string player, int xOnA, int xOnB)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Gap #2 — No room ownership across nodes (split brain)");
        sb.AppendLine();
        sb.AppendLine("**Status: demonstrate-only.** This cannot be fixed inside a single process — it");
        sb.AppendLine("requires cross-node coordination. The test stands up two independent server nodes");
        sb.AppendLine("(what two processes behind a load balancer would be) and drives the *same* room.");
        sb.AppendLine();
        sb.AppendLine($"The same identity `{tenant}/{room}/{player}` acted on each node: 5 moves on A, 2 on B.");
        sb.AppendLine();
        sb.AppendLine("| node | authoritative `p1.x` |");
        sb.AppendLine("|---|---:|");
        sb.AppendLine($"| A | {xOnA} |");
        sb.AppendLine($"| B | {xOnB} |");
        sb.AppendLine();
        sb.AppendLine($"Same `RoomKey({tenant}, {room})`, two different authoritative truths. A client that");
        sb.AppendLine("reconnects (or is rehashed by the load balancer) to the other node sees a different");
        sb.AppendLine("world. Each node created the room locally on first join; neither checked for an owner.");
        sb.AppendLine();
        sb.AppendLine("## What's missing to handle this");
        sb.AppendLine();
        sb.AppendLine("- A **room→node ownership registry**: exactly one node owns a given `RoomKey` at a time.");
        sb.AppendLine("- **Sticky routing / node affinity**: a client for a room is always routed to its owner.");
        sb.AppendLine("- **Ownership handoff + fencing** on node failure, so a room moves owners without two");
        sb.AppendLine("  nodes ticking it at once.");
        sb.AppendLine();
        sb.AppendLine("Until those exist, the README's \"horizontally scalable\" claim is aspirational: the");
        sb.AppendLine("kernel is single-node-correct, but nothing coordinates rooms across nodes.");
        return sb.ToString();
    }
}
