namespace GameServer.Matchmaking;

/// <summary>
/// A proposed match: a set of tickets a match function thinks should play together, all from one
/// <see cref="Scope"/>. It is a CANDIDATE — the evaluator may discard it if it overlaps a better one.
/// </summary>
/// <remarks>
/// Every constructed candidate is validated to be single-scope: a match function physically cannot
/// emit a cross-tenant/cross-version match, because the constructor throws if the tickets disagree on
/// scope. That makes the isolation invariant enforced at the type boundary, not just by convention.
/// <see cref="Quality"/> lets the evaluator prefer better matches when candidates overlap; higher is
/// better.
/// </remarks>
public sealed class CandidateMatch
{
    public CandidateMatch(MatchScope scope, IReadOnlyList<MatchTicket> tickets, double quality = 0)
    {
        if (tickets is null || tickets.Count == 0)
        {
            throw new ArgumentException("A candidate match needs at least one ticket.", nameof(tickets));
        }

        foreach (var ticket in tickets)
        {
            if (ticket.Scope != scope)
            {
                throw new ArgumentException(
                    $"Candidate match crosses scopes: ticket '{ticket.TicketId}' is {ticket.Scope}, match is {scope}.",
                    nameof(tickets));
            }
        }

        Scope = scope;
        Tickets = tickets;
        Quality = quality;
    }

    public MatchScope Scope { get; }

    public IReadOnlyList<MatchTicket> Tickets { get; }

    public double Quality { get; }

    public IEnumerable<string> TicketIds => Tickets.Select(t => t.TicketId);
}

/// <summary>
/// Forms candidate matches from the tickets a single pool admits. A match function NEVER sees more
/// than one scope (it is handed a scope-bound pool), so it cannot form a cross-scope match even by
/// mistake.
/// </summary>
public interface IMatchFunction
{
    /// <summary>
    /// Proposes candidate matches over <paramref name="tickets"/> (all already scope-filtered by the
    /// pool). Implementations must not assume any particular ordering and must be deterministic given
    /// the same input.
    /// </summary>
    IReadOnlyList<CandidateMatch> Form(MatchScope scope, IReadOnlyList<MatchTicket> tickets);
}
