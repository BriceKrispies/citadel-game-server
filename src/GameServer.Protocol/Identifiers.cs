namespace GameServer.Protocol;

// Strongly-typed identifiers. These are the pure shared-kernel primitives:
// they carry no behavior, no infrastructure, and may be referenced by any layer
// (including Simulation) without violating the infrastructure-free rule.
// Using distinct types prevents accidentally passing a RoomId where a PlayerId
// is expected.

/// <summary>Identifies a tenant. Tenant isolation is enforced everywhere this flows.</summary>
public readonly record struct TenantId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>Identifies a game (catalog entry) within a tenant.</summary>
public readonly record struct GameId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>Identifies an authoritative room (simulation state owner).</summary>
public readonly record struct RoomId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>Identifies a logical session for a connected client.</summary>
public readonly record struct SessionId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>Identifies a player participating in a room.</summary>
public readonly record struct PlayerId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>Identifies a physical client connection at the transport edge.</summary>
public readonly record struct ConnectionId(string Value)
{
    public override string ToString() => Value;
}
