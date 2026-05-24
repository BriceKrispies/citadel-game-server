using System.Buffers.Binary;
using GameServer.Protocol;
using GameServer.Replication;
using GameServer.Simulation;
using Xunit;

namespace GameServer.Transport;

/// <summary>
/// Proves the room-over-IPC seam without a real process: an in-process channel routes
/// RemoteGameRoom's wire requests straight into a RoomHost wrapping a real GameRoom. The
/// headline guarantee is Liskov parity — a RemoteGameRoom must behave indistinguishably from
/// the local GameRoom it proxies, across every IGameRoom member — so the routing layer can
/// swap one for the other. Also covers error propagation and the stdio channel's wire shape.
/// </summary>
public sealed class RoomIpcTests
{
    // Routes a request line directly into the host's dispatcher: same code path as stdio, no
    // threads or process, fully deterministic.
    private sealed class InProcessChannel : IRoomRpcChannel
    {
        private readonly RoomHost _host;
        public InProcessChannel(RoomHost host) => _host = host;
        public string Exchange(string requestLine) => _host.Handle(requestLine);
        public void Dispose() { }
    }

    private static GameRoom NewGameRoom() =>
        new(new RoomId("arena"), new MoveRightGame(), new LogicalSimulationClock(), new SeededRandomSource());

    private static RemoteGameRoom Remote(IGameRoom hosted) =>
        new(new RoomId("arena"), new InProcessChannel(new RoomHost(hosted)));

    [Fact]
    public void RemoteRoom_MatchesLocalRoom_AcrossTheFullLifecycle()
    {
        var local = NewGameRoom();
        var remote = Remote(NewGameRoom());

        var player = new PlayerId("p1");
        foreach (var room in new IGameRoom[] { local, remote })
        {
            room.Join(player);
        }

        // Membership + admission parity.
        Assert.Equal(local.HasPlayer(player), remote.HasPlayer(player));
        Assert.True(remote.HasPlayer(player));
        Assert.Equal(
            local.TryEnqueue(player, MoveRightGame.MoveRight, 1),
            remote.TryEnqueue(player, MoveRightGame.MoveRight, 1));
        Assert.Equal(CommandAdmission.RejectedInvalidCommand, remote.TryEnqueue(player, "bogus", 2));
        Assert.Equal(local.QueueDepth, remote.QueueDepth);

        // Tick parity: same authoritative tick, same events, byte-identical opaque state.
        var localTick = local.Tick();
        var remoteTick = remote.Tick();
        Assert.Equal(localTick.Snapshot.Tick, remoteTick.Snapshot.Tick);
        Assert.Equal(localTick.Snapshot.State, remoteTick.Snapshot.State);
        Assert.Equal(localTick.Events.Count, remoteTick.Events.Count);
        Assert.Equal(localTick.Events[0].Command, remoteTick.Events[0].Command);
        Assert.Equal(localTick.Events[0].Player, remoteTick.Events[0].Player);

        // Projection parity, including the opaque per-entity payload bytes crossing the wire.
        var localEntities = local.Project();
        var remoteEntities = remote.Project();
        Assert.Equal(localEntities.Count, remoteEntities.Count);
        Assert.Equal(localEntities[0].Id, remoteEntities[0].Id);
        Assert.Equal(localEntities[0].Version, remoteEntities[0].Version);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(remoteEntities[0].Payload)); // one MoveRight applied
    }

    [Fact]
    public void RemoteRoom_RoundTripsSnapshotRestore()
    {
        var remote = Remote(NewGameRoom());
        var player = new PlayerId("p1");
        remote.Join(player);
        remote.TryEnqueue(player, MoveRightGame.MoveRight, 1);
        var afterTick = remote.Tick();

        // A fresh remote room restored from that snapshot reports the same tick + state bytes.
        var restored = Remote(NewGameRoom());
        restored.RestoreFrom(afterTick.Snapshot);
        var snap = restored.Snapshot();
        Assert.Equal(afterTick.Snapshot.Tick, snap.Tick);
        Assert.Equal(afterTick.Snapshot.State, snap.State);
    }

    private sealed class ThrowingRoom : IGameRoom
    {
        public RoomId Id => new("arena");
        public int QueueDepth => 0;
        public void Join(PlayerId player) { }
        public bool HasPlayer(PlayerId player) => false;
        public CommandAdmission TryEnqueue(PlayerId player, string command, long sequence) => CommandAdmission.Accepted;
        public TickResult Tick() => throw new InvalidOperationException("simulated room failure");
        public IReadOnlyList<EntitySnapshot> Project() => Array.Empty<EntitySnapshot>();
        public RoomSnapshot Snapshot() => new(0, Array.Empty<byte>());
        public void RestoreFrom(RoomSnapshot snapshot) { }
        public bool ApplyRecoveredEvent(RoomEvent recoveredEvent) => true;
    }

    [Fact]
    public void HostFailure_SurfacesAsRemoteRoomException_NotATornStream()
    {
        var remote = Remote(new ThrowingRoom());
        var ex = Assert.Throws<RemoteRoomException>(() => remote.Tick());
        Assert.Contains("simulated room failure", ex.Message);
    }

    [Fact]
    public void StreamChannel_WritesRequest_AndReturnsResponseLine()
    {
        var sent = new StringWriter();
        using var channel = new StreamRoomRpcChannel(sent, new StringReader("the-response\n"));

        var response = channel.Exchange("the-request");

        Assert.Equal("the-response", response);
        Assert.Contains("the-request", sent.ToString());
    }

    [Fact]
    public void StreamChannel_Throws_WhenChildClosedTheChannel()
    {
        using var channel = new StreamRoomRpcChannel(new StringWriter(), new StringReader(string.Empty));
        Assert.Throws<RemoteRoomException>(() => channel.Exchange("req"));
    }
}
