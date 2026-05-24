using GameServer.Protocol;
using GameServer.Tenancy;

namespace GameServer.Routing;

/// <summary>
/// A logical session for a connected client. Binds a session id to its resolved
/// tenant context so the tenant is carried explicitly and never re-resolved in
/// the hot path.
/// </summary>
public sealed record Session(SessionId Id, TenantContext Tenant);
