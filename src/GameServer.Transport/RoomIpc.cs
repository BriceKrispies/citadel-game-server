using System.Text.Json;
using System.Text.Json.Serialization;
using GameServer.Protocol;
using GameServer.Replication;
using GameServer.Simulation;

namespace GameServer.Transport;

// ============================================================================
// Room-over-IPC: runs an authoritative room's loop in a SEPARATE process while the
// parent keeps connections, fan-out, and persistence. The parent talks to the child
// through RemoteGameRoom, a substitutable IGameRoom that marshals each call over a
// line-delimited JSON RPC; the child runs the real GameRoom behind a RoomHost. Because
// the data plane already serializes room access under a per-room lock, the RPC is a
// strict request→response with no pipelining or correlation ids.
//
// This is the payload that rides on the OutOfProcessWorker lifecycle: that worker owns
// start/restart/kill of the child; this owns the room protocol over its stdio.
// ============================================================================

/// <summary>Raised when a remote room call fails or the child closes the channel.</summary>
public sealed class RemoteRoomException : Exception
{
    public RemoteRoomException(RoomId room, string message) : base($"Remote room '{room.Value}': {message}") { }
}

/// <summary>
/// A strictly-serialized request/response channel for one remote room: write one request
/// line, read exactly one response line. The seam that lets <see cref="RemoteGameRoom"/> be
/// tested in-process and run over real stdio in production.
/// </summary>
public interface IRoomRpcChannel : IDisposable
{
    string Exchange(string requestLine);
}

/// <summary>The room operations marshalled across the boundary — one per <see cref="IGameRoom"/> member.</summary>
internal enum RoomRpcOp
{
    QueueDepth,
    CanJoin,
    Join,
    Leave,
    Terminate,
    HasPlayer,
    TryEnqueue,
    Tick,
    Project,
    Snapshot,
    RestoreFrom,
    ApplyRecoveredEvent,
}

internal sealed record RoomRpcRequest(
    RoomRpcOp Op,
    string? Player = null,
    string? Command = null,
    long Sequence = 0,
    RoomSnapshotDto? Snapshot = null,
    RoomEventDto? Event = null);

internal sealed record RoomRpcResponse(
    bool? Flag = null,
    int? Count = null,
    string? Admission = null,
    TickResultDto? Tick = null,
    IReadOnlyList<EntitySnapshotDto>? Entities = null,
    RoomSnapshotDto? Snapshot = null,
    string? Error = null);

internal sealed record RoomSnapshotDto(long Tick, byte[] State);

internal sealed record RoomEventDto(long Tick, string Player, string Command);

internal sealed record TickResultDto(RoomSnapshotDto Snapshot, IReadOnlyList<RoomEventDto> Events);

internal sealed record EntitySnapshotDto(string Id, long Version, double X, double Y, string Group, byte[] Payload);

/// <summary>(De)serializes the room RPC to/from a single JSON line and maps wire DTOs to domain types.</summary>
internal static class RoomRpcCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Encode(RoomRpcRequest request) => JsonSerializer.Serialize(request, Options);

    public static string Encode(RoomRpcResponse response) => JsonSerializer.Serialize(response, Options);

    public static RoomRpcRequest DecodeRequest(string line) =>
        JsonSerializer.Deserialize<RoomRpcRequest>(line, Options) ?? throw new FormatException("empty room RPC request");

    public static RoomRpcResponse DecodeResponse(string line) =>
        JsonSerializer.Deserialize<RoomRpcResponse>(line, Options) ?? throw new FormatException("empty room RPC response");

    public static RoomSnapshotDto ToDto(RoomSnapshot s) => new(s.Tick, s.State);

    public static RoomSnapshot ToDomain(RoomSnapshotDto d) => new(d.Tick, d.State);

    public static RoomEventDto ToDto(RoomEvent e) => new(e.Tick, e.Player.Value, e.Command);

    public static RoomEvent ToDomain(RoomEventDto d) => new(d.Tick, new PlayerId(d.Player), d.Command);

    public static TickResultDto ToDto(TickResult t) => new(ToDto(t.Snapshot), t.Events.Select(ToDto).ToArray());

    public static TickResult ToDomain(TickResultDto d) => new(ToDomain(d.Snapshot), d.Events.Select(ToDomain).ToArray());

    public static EntitySnapshotDto ToDto(EntitySnapshot e) =>
        new(e.Id.Value, e.Version, e.Key.X, e.Key.Y, e.Key.Group, e.Payload);

    public static EntitySnapshot ToDomain(EntitySnapshotDto d) =>
        new(new EntityId(d.Id), d.Version, new RelevanceKey(d.X, d.Y, d.Group), d.Payload);
}

/// <summary>
/// A parent-side <see cref="IGameRoom"/> that proxies every call to a room running in a child
/// process over an <see cref="IRoomRpcChannel"/>. It is honestly substitutable: the routing
/// layer's room factory can hand one of these back in place of a local <see cref="GameRoom"/>
/// without the data plane knowing the room lives in another process.
/// </summary>
public sealed class RemoteGameRoom : IGameRoom, IDisposable
{
    private readonly IRoomRpcChannel _channel;

    public RemoteGameRoom(RoomId id, IRoomRpcChannel channel)
    {
        Id = id;
        _channel = channel;
    }

    public RoomId Id { get; }

    public int QueueDepth => Call(new RoomRpcRequest(RoomRpcOp.QueueDepth)).Count ?? 0;

    public bool CanJoin(PlayerId player) => Call(new RoomRpcRequest(RoomRpcOp.CanJoin, Player: player.Value)).Flag ?? false;

    public void Join(PlayerId player) => Call(new RoomRpcRequest(RoomRpcOp.Join, Player: player.Value));

    public void Leave(PlayerId player) => Call(new RoomRpcRequest(RoomRpcOp.Leave, Player: player.Value));

    public void Terminate() => Call(new RoomRpcRequest(RoomRpcOp.Terminate));

    public bool HasPlayer(PlayerId player) => Call(new RoomRpcRequest(RoomRpcOp.HasPlayer, Player: player.Value)).Flag ?? false;

    public CommandAdmission TryEnqueue(PlayerId player, string command, long sequence) =>
        Enum.Parse<CommandAdmission>(Call(new RoomRpcRequest(RoomRpcOp.TryEnqueue, player.Value, command, sequence)).Admission
            ?? throw new RemoteRoomException(Id, "missing admission result"));

    public TickResult Tick() =>
        RoomRpcCodec.ToDomain(Call(new RoomRpcRequest(RoomRpcOp.Tick)).Tick ?? throw new RemoteRoomException(Id, "missing tick result"));

    public IReadOnlyList<EntitySnapshot> Project() =>
        (Call(new RoomRpcRequest(RoomRpcOp.Project)).Entities ?? throw new RemoteRoomException(Id, "missing projection"))
            .Select(RoomRpcCodec.ToDomain).ToArray();

    public RoomSnapshot Snapshot() =>
        RoomRpcCodec.ToDomain(Call(new RoomRpcRequest(RoomRpcOp.Snapshot)).Snapshot ?? throw new RemoteRoomException(Id, "missing snapshot"));

    public void RestoreFrom(RoomSnapshot snapshot) =>
        Call(new RoomRpcRequest(RoomRpcOp.RestoreFrom, Snapshot: RoomRpcCodec.ToDto(snapshot)));

    public bool ApplyRecoveredEvent(RoomEvent recoveredEvent) =>
        Call(new RoomRpcRequest(RoomRpcOp.ApplyRecoveredEvent, Event: RoomRpcCodec.ToDto(recoveredEvent))).Flag ?? false;

    private RoomRpcResponse Call(RoomRpcRequest request)
    {
        var response = RoomRpcCodec.DecodeResponse(_channel.Exchange(RoomRpcCodec.Encode(request)));
        if (response.Error is { } error)
        {
            throw new RemoteRoomException(Id, error);
        }

        return response;
    }

    public void Dispose() => _channel.Dispose();
}

/// <summary>
/// The child-side dispatcher: receives room RPC lines, applies them to the real
/// <see cref="IGameRoom"/> it hosts, and writes back result lines. A host exception is
/// returned as a typed error response rather than crashing the protocol stream.
/// </summary>
public sealed class RoomHost
{
    private readonly IGameRoom _room;

    public RoomHost(IGameRoom room) => _room = room;

    /// <summary>
    /// Serves RPC over a duplex of text lines until <paramref name="input"/> reaches EOF.
    /// EOF (the parent closing its end) is the cooperative-stop signal: the loop returns and
    /// the child process exits. Nothing else may be written to <paramref name="output"/>.
    /// </summary>
    public void Run(TextReader input, TextWriter output)
    {
        string? line;
        while ((line = input.ReadLine()) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }

            output.WriteLine(Handle(line));
            output.Flush();
        }
    }

    /// <summary>Handles one request line and returns one response line (no I/O; unit-testable).</summary>
    public string Handle(string requestLine)
    {
        RoomRpcRequest request;
        try
        {
            request = RoomRpcCodec.DecodeRequest(requestLine);
        }
        catch (Exception ex)
        {
            return RoomRpcCodec.Encode(new RoomRpcResponse(Error: $"malformed request: {ex.Message}"));
        }

        try
        {
            return RoomRpcCodec.Encode(Dispatch(request));
        }
        catch (Exception ex)
        {
            // Surface the failure to the parent as data, not as a torn stream.
            return RoomRpcCodec.Encode(new RoomRpcResponse(Error: ex.Message));
        }
    }

    private RoomRpcResponse Dispatch(RoomRpcRequest r) => r.Op switch
    {
        RoomRpcOp.QueueDepth => new RoomRpcResponse(Count: _room.QueueDepth),
        RoomRpcOp.CanJoin => new RoomRpcResponse(Flag: _room.CanJoin(new PlayerId(Require(r.Player, "player")))),
        RoomRpcOp.Join => Ack(() => _room.Join(new PlayerId(Require(r.Player, "player")))),
        RoomRpcOp.Leave => Ack(() => _room.Leave(new PlayerId(Require(r.Player, "player")))),
        RoomRpcOp.Terminate => Ack(() => _room.Terminate()),
        RoomRpcOp.HasPlayer => new RoomRpcResponse(Flag: _room.HasPlayer(new PlayerId(Require(r.Player, "player")))),
        RoomRpcOp.TryEnqueue => new RoomRpcResponse(
            Admission: _room.TryEnqueue(new PlayerId(Require(r.Player, "player")), Require(r.Command, "command"), r.Sequence).ToString()),
        RoomRpcOp.Tick => new RoomRpcResponse(Tick: RoomRpcCodec.ToDto(_room.Tick())),
        RoomRpcOp.Project => new RoomRpcResponse(Entities: _room.Project().Select(RoomRpcCodec.ToDto).ToArray()),
        RoomRpcOp.Snapshot => new RoomRpcResponse(Snapshot: RoomRpcCodec.ToDto(_room.Snapshot())),
        RoomRpcOp.RestoreFrom => Ack(() => _room.RestoreFrom(RoomRpcCodec.ToDomain(r.Snapshot ?? throw new ArgumentException("missing snapshot")))),
        RoomRpcOp.ApplyRecoveredEvent => new RoomRpcResponse(
            Flag: _room.ApplyRecoveredEvent(RoomRpcCodec.ToDomain(r.Event ?? throw new ArgumentException("missing event")))),
        _ => new RoomRpcResponse(Error: $"unknown op {r.Op}"),
    };

    private static RoomRpcResponse Ack(Action action)
    {
        action();
        return new RoomRpcResponse(Flag: true);
    }

    private static string Require(string? value, string name) =>
        value ?? throw new ArgumentException($"missing {name}");
}

/// <summary>
/// The production <see cref="IRoomRpcChannel"/>: writes requests to the child's stdin and reads
/// responses from its stdout. Disposing closes the writer, signalling EOF to the child (the
/// cooperative stop). Access is serialized so a request and its response never interleave.
/// </summary>
public sealed class StreamRoomRpcChannel : IRoomRpcChannel
{
    private readonly TextWriter _toChild;
    private readonly TextReader _fromChild;
    private readonly object _gate = new();

    public StreamRoomRpcChannel(TextWriter toChild, TextReader fromChild)
    {
        _toChild = toChild;
        _fromChild = fromChild;
    }

    public string Exchange(string requestLine)
    {
        lock (_gate)
        {
            _toChild.WriteLine(requestLine);
            _toChild.Flush();
            return _fromChild.ReadLine() ?? throw new RemoteRoomException(default, "child closed the channel");
        }
    }

    public void Dispose() => _toChild.Dispose();
}
