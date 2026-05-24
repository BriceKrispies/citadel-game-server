using GameServer.Identity;
using GameServer.Protocol;

namespace GameServer.Transport.Testing;

/// <summary>
/// A scripted in-process client over an <see cref="InMemoryBidirectionalTransport"/>.
/// Builds well-formed envelopes with a monotonic per-client sequence, and exposes
/// the server-pushed messages it received. Cross-feature test support (drives the
/// realtime edge end to end), so it lives in <c>Testing/</c>.
/// </summary>
public sealed class FakeClient
{
    private readonly InMemoryBidirectionalTransport _transport;
    private readonly TenantId _tenant;
    private readonly GameId _game;
    private readonly RoomId _room;
    private readonly PlayerId _player;
    private long _sequence;

    public FakeClient(InMemoryBidirectionalTransport transport, TenantId tenant, GameId game, PlayerId player, RoomId room)
    {
        _transport = transport;
        _tenant = tenant;
        _game = game;
        _player = player;
        _room = room;
    }

    /// <summary>
    /// The verified identity the realtime edge would derive from this client's join
    /// token (tenant/game/room/player it is authorized for). Tests pass this to
    /// <c>RealtimeServer.HandleConnectionAsync</c> in place of doing real verification;
    /// the validity window is irrelevant to the server (the edge checks expiry).
    /// </summary>
    public JoinTokenClaims Principal =>
        new(_tenant.Value, _game.Value, _room.Value, _player.Value, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1));

    public void Hello(int protocolVersion = ProtocolVersions.Current, string clientName = "fake-client") =>
        _transport.ClientSend(Envelope(
            MessageType.ClientHello, new ClientHello(clientName),
            protocolVersion: protocolVersion, includePlayer: false));

    public void Join(RoomId room) =>
        _transport.ClientSend(Envelope(
            MessageType.ClientJoinRoom, new ClientJoinRoom(room), room: room));

    public void Command(string command, RoomId room, long? sequence = null) =>
        _transport.ClientSend(Envelope(
            MessageType.ClientCommand, new ClientCommand(command), room: room, sequence: sequence));

    /// <summary>Acknowledges receipt+apply of server state up through <paramref name="ackedServerTick"/>.</summary>
    public void Ack(long ackedServerTick, RoomId? room = null, long ackedSequence = 0) =>
        _transport.ClientSend(Envelope(
            MessageType.ClientAck, new ClientAck(ackedSequence, ackedServerTick), room: room));

    /// <summary>Signals the client has closed the connection so the server loop completes.</summary>
    public void Close() => _transport.CompleteClient();

    /// <summary>All messages the server pushed to this client so far, in order.</summary>
    public IReadOnlyList<MessageEnvelope> Received() => _transport.DrainOutbound();

    private MessageEnvelope Envelope(
        MessageType type,
        IMessagePayload payload,
        int protocolVersion = ProtocolVersions.Current,
        RoomId? room = null,
        long? sequence = null,
        bool includePlayer = true)
    {
        return new MessageEnvelope
        {
            TenantId = _tenant,
            GameId = _game,
            RoomId = room,
            SessionId = null,
            PlayerId = includePlayer ? _player : null,
            ProtocolVersion = protocolVersion,
            MessageType = type,
            Sequence = sequence ?? ++_sequence,
            TraceId = $"trace-{type}-{_sequence}",
            Payload = payload,
        };
    }
}
