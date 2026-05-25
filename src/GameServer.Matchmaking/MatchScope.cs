using GameServer.Protocol;

namespace GameServer.Matchmaking;

/// <summary>
/// The isolation boundary for matchmaking: a (tenant, game, version) triple. Every ticket and every
/// pool carries one, and a match function only ever sees tickets from a SINGLE scope — because the
/// scope is part of the pool's identity (see <see cref="PoolKey"/>), not a filter applied late.
/// </summary>
/// <remarks>
/// This is the security-critical invariant of Wave 5: a player from tenant A's game must never be put
/// in the same room as tenant B's player, nor a v1 client matched against a v2 client. Baking the
/// scope into the key (rather than filtering after candidate matches are formed) makes cross-scope
/// bleed structurally impossible: tickets in different scopes land in different pools and are never
/// in the same candidate set. <see cref="GameVersion"/> is the protocol/game version a client speaks,
/// so a fleet mid-rollout never co-mingles incompatible clients.
/// </remarks>
public readonly record struct MatchScope(TenantId TenantId, GameId GameId, int GameVersion)
{
    public override string ToString() => $"{TenantId.Value}/{GameId.Value}/v{GameVersion}";
}
