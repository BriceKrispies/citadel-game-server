using GameServer.Replication;

namespace GameServer.ControlPlane;

// Stable HTTP control-plane DTOs. These are part of the public platform contract
// (see contracts/http/openapi.md). They carry no gameplay logic.

public sealed record GameSummary(string GameId, string Name);

/// <summary>
/// Game catalog entry. <see cref="Replication"/> is the per-game replication policy
/// the realtime data plane applies (interest, delta, budget). Null means the
/// platform default (everyone/full, batched).
/// </summary>
public sealed record GameDetail(string GameId, string Name, string Description, int ProtocolVersion, ReplicationPolicy? Replication = null);

public sealed record CreateRoomRequest(string TenantId, string GameId);

public sealed record RoomContract(string RoomId, string TenantId, string GameId, string Status);

public sealed record CreateJoinTokenRequest(string PlayerId);

public sealed record JoinTokenContract(string Token, string TenantId, string GameId, string RoomId, string PlayerId);

public sealed record CreateSessionRequest(string TenantId, string PlayerId);

public sealed record SessionContract(string SessionId, string TenantId, string PlayerId, string Status);

/// <summary>Typed HTTP error body. <c>Code</c> aligns with the realtime ErrorCode vocabulary.</summary>
public sealed record ApiError(string Code, string Message);
