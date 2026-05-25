namespace GameServer.ControlPlane;

// Stable HTTP control-plane DTOs. These are part of the public platform contract
// (see contracts/http/openapi.md). They carry no gameplay logic.

// GameSummary / GameDetail are PORT DTOs and live in the universal kernel (GameServer.Abstractions,
// namespace GameServer.ControlPlane) alongside IGameRegistry, so a rank-1 durable adapter can implement
// the registry without a sideways ring dependency.

public sealed record CreateRoomRequest(string TenantId, string GameId);

public sealed record RoomContract(string RoomId, string TenantId, string GameId, string Status);

public sealed record CreateJoinTokenRequest(string PlayerId);

public sealed record JoinTokenContract(string Token, string TenantId, string GameId, string RoomId, string PlayerId);

public sealed record CreateSessionRequest(string TenantId, string PlayerId);

public sealed record SessionContract(string SessionId, string TenantId, string PlayerId, string Status);

/// <summary>Typed HTTP error body. <c>Code</c> aligns with the realtime ErrorCode vocabulary.</summary>
public sealed record ApiError(string Code, string Message);

// ---- Wave 7 control-plane CRUD/admin DTOs -----------------------------------

/// <summary>Provision a tenant (platform-admin). The id must be unique across the platform.</summary>
public sealed record CreateTenantRequest(string TenantId, string DisplayName);

/// <summary>Register a game owned by a tenant (game-admin of that tenant, or platform-admin).</summary>
public sealed record CreateGameRequest(string TenantId, string GameId, string Name, string Description, int ProtocolVersion);

/// <summary>Register a new schema version of an existing game.</summary>
public sealed record CreateGameVersionRequest(int SchemaVersion, string Notes);

/// <summary>Read/update the realtime admission ceilings (platform-admin).</summary>
public sealed record AdmissionLimitsContract(int MaxConnections, int MaxConnectionsPerTenant, int MaxRooms, int MaxRoomsPerTenant);

/// <summary>
/// One available room for a game, as returned by <c>GET /api/v1/games/{gameId}/rooms</c>. Carries the
/// control-plane record (RoomId/Status) plus a snapshot of LIVE realtime state when the room is running
/// (<see cref="Live"/> true → <see cref="Tick"/>/<see cref="SubscriberCount"/> from the data plane);
/// a created-but-never-joined room reports <see cref="Live"/> false with tick/subscribers 0.
/// </summary>
public sealed record RoomSummaryContract(
    string RoomId, string GameId, string TenantId, string Status, int SubscriberCount, long Tick, bool Live);

/// <summary>The available rooms for a game in the caller's tenant (always at least one).</summary>
public sealed record GameRoomsContract(string GameId, IReadOnlyList<RoomSummaryContract> Rooms);
