using System.Buffers.Binary;
using System.Diagnostics;
using GameServer.Protocol;
using GameServer.Simulation;
using GameServer.Transport;

namespace GameServer.IntegrationTests;

/// <summary>
/// End-to-end proof that an authoritative room's tick loop runs in a SEPARATE OS process: the
/// parent drives a <see cref="RemoteGameRoom"/> over real stdio against a child
/// <c>GameServer.RoomWorkerHost</c> running the real <see cref="GameRoom"/>. Join → command →
/// tick → projection cross the process boundary and come back authoritative; closing stdin
/// stops the child cleanly (no orphan). Real process + real wall-clock — an integration
/// scenario, kept out of the deterministic fast loop.
/// </summary>
public sealed class RoomInChildProcessScenario
{
    [Fact]
    public void RoomTickLoop_RunsInAChildProcess_OverIpc()
    {
        var dll = ChildHostDll();
        Assert.True(File.Exists(dll), $"child worker host not built at {dll}");

        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\" arena move-right")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start child worker host");
        try
        {
            var channel = new StreamRoomRpcChannel(process.StandardInput, process.StandardOutput);
            var room = new RemoteGameRoom(new RoomId("arena"), channel);

            var player = new PlayerId("p1");
            room.Join(player);
            Assert.True(room.HasPlayer(player));
            Assert.Equal(CommandAdmission.Accepted, room.TryEnqueue(player, MoveRightGame.MoveRight, 1));
            Assert.Equal(1, room.QueueDepth);

            // The authoritative tick happened in the child and returned across the boundary.
            var result = room.Tick();
            Assert.Equal(1, result.Snapshot.Tick);
            Assert.Single(result.Events);
            Assert.Equal(MoveRightGame.MoveRight, result.Events[0].Command);

            // The opaque per-entity payload (little-endian int32 X) survived serialization both ways.
            var entities = room.Project();
            Assert.Single(entities);
            Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(entities[0].Payload));

            // Closing stdin is the cooperative stop: the child sees EOF and exits cleanly.
            channel.Dispose();
            Assert.True(process.WaitForExit(5000), "child did not exit after stdin close");
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    // The child exe is ProjectReferenced (so it is built), but a referenced exe's dll is copied
    // to this test's output WITHOUT its runtimeconfig.json — so launch it from its own build
    // output, derived from this assembly's location: <repo>/tests/<proj>/bin/<Config>/<Tfm>/.
    private static string ChildHostDll()
    {
        var tfmDir = new DirectoryInfo(AppContext.BaseDirectory);
        var config = tfmDir.Parent!.Name; // Debug | Release
        var tfm = tfmDir.Name;            // net10.0
        var repoRoot = tfmDir.Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
        return Path.Combine(repoRoot, "src", "GameServer.RoomWorkerHost", "bin", config, tfm, "GameServer.RoomWorkerHost.dll");
    }
}
