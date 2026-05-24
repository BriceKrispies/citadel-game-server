namespace GameServer.Protocol;

/// <summary>
/// The versioned envelope that wraps every protocol message. Identity fields that
/// do not yet exist at a given point in the handshake are nullable (e.g. a
/// <see cref="ClientHello"/> has no session, room, or player yet).
/// </summary>
/// <remarks>
/// The envelope is immutable. Construct derived envelopes with <c>with</c> rather
/// than mutating, so a message can never be partially rewritten in flight.
/// </remarks>
public sealed record MessageEnvelope
{
    public required TenantId TenantId { get; init; }
    public required GameId GameId { get; init; }
    public RoomId? RoomId { get; init; }
    public SessionId? SessionId { get; init; }
    public PlayerId? PlayerId { get; init; }

    public required int ProtocolVersion { get; init; }
    public required MessageType MessageType { get; init; }

    /// <summary>Monotonic per-sender sequence used to reject stale/duplicate input.</summary>
    public required long Sequence { get; init; }

    /// <summary>Correlates this message to logs, telemetry, and any resulting reply.</summary>
    public required string TraceId { get; init; }

    public required IMessagePayload Payload { get; init; }

    /// <summary>
    /// True when the envelope's declared <see cref="MessageType"/> matches the
    /// actual payload type. The codec and routing rely on this invariant.
    /// </summary>
    public bool IsConsistent => Payload.Type == MessageType;
}
