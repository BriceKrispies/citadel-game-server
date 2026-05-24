namespace GameServer.Protocol;

/// <summary>
/// Marker for a typed protocol payload. Every payload self-declares its
/// <see cref="MessageType"/> so the envelope's declared type can be validated
/// against the actual payload shape.
/// </summary>
public interface IMessagePayload
{
    MessageType Type { get; }
}

/// <summary>Stable, typed error codes so clients and tests never match on strings.</summary>
public enum ServerErrorCode
{
    UnknownTenant,
    UnsupportedProtocolVersion,
    InvalidCommand,
    StaleSequence,
    NotJoined,
    RoomUnavailable,
    MalformedMessage,
    Unauthorized,
}

// ---- Client -> Server -------------------------------------------------------

/// <summary>First message on a connection. Requested protocol version lives on the envelope.</summary>
public sealed record ClientHello(string ClientName) : IMessagePayload
{
    public MessageType Type => MessageType.ClientHello;
}

/// <summary>Request to join (or create) a room within the resolved tenant.</summary>
public sealed record ClientJoinRoom(RoomId RoomId) : IMessagePayload
{
    public MessageType Type => MessageType.ClientJoinRoom;
}

/// <summary>
/// An intent (not a truth claim) to mutate room state on a future tick. The command
/// is a game-defined string; the room's game validates and interprets it.
/// </summary>
public sealed record ClientCommand(string Command) : IMessagePayload
{
    public MessageType Type => MessageType.ClientCommand;
}

/// <summary>
/// Confirms the client has received and applied server state up through
/// <see cref="AckedServerTick"/>. This is what advances a viewer's delta baseline:
/// the server may only assume the client holds a snapshot once the client says so,
/// never because a write to the socket succeeded.
/// </summary>
public sealed record ClientAck(long AckedSequence, long AckedServerTick) : IMessagePayload
{
    public MessageType Type => MessageType.ClientAck;
}

// ---- Server -> Client -------------------------------------------------------

/// <summary>Issued once a client is accepted and a session exists.</summary>
public sealed record ServerWelcome(SessionId SessionId, int AcceptedProtocolVersion) : IMessagePayload
{
    public MessageType Type => MessageType.ServerWelcome;
}

/// <summary>
/// One projected entity in a snapshot: an opaque, game-defined <see cref="Payload"/>
/// addressed by a stable <see cref="EntityId"/>. The platform never interprets the
/// payload — only the game and its client do.
/// </summary>
public sealed record EntityState(string EntityId, byte[] Payload);

/// <summary>
/// Authoritative state for a tick: the entities this client should see, each carrying
/// the game's opaque bytes. One snapshot per client per tick carries all relevant
/// entities (batching); in delta mode it carries only those that changed.
/// </summary>
public sealed record ServerSnapshot(long Tick, IReadOnlyList<EntityState> Entities) : IMessagePayload
{
    public MessageType Type => MessageType.ServerSnapshot;
}

/// <summary>A typed, traceable rejection. Always correlated to the offending command via the envelope.</summary>
public sealed record ServerError(ServerErrorCode Code, string Message) : IMessagePayload
{
    public MessageType Type => MessageType.ServerError;
}
