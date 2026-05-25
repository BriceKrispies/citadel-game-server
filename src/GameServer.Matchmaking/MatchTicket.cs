using GameServer.Protocol;

namespace GameServer.Matchmaking;

/// <summary>
/// A player's intent to be matched, plus the attributes a match function may rank/bucket on. A ticket
/// is INTENT, not truth: it never carries a room or a server-trusted position — only what the player
/// asks for (a scope, optional skill, optional free-form attributes).
/// </summary>
/// <remarks>
/// <see cref="Scope"/> is the tenant/game/version isolation boundary — a ticket can only ever be
/// matched within its own scope. <see cref="EnqueuedSeconds"/> is the monotonic time the ticket
/// entered the queue, captured through an injected clock (never wall-clock), so age-based fairness is
/// deterministic under test.
/// </remarks>
public sealed record MatchTicket(
    string TicketId,
    MatchScope Scope,
    PlayerId PlayerId,
    int Skill,
    double EnqueuedSeconds,
    IReadOnlyDictionary<string, string>? Attributes = null)
{
    /// <summary>Reads a string attribute, or null if absent. Attributes are optional, free-form intent.</summary>
    public string? Attribute(string key) =>
        Attributes is not null && Attributes.TryGetValue(key, out var value) ? value : null;
}
