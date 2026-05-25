using System.Collections.Concurrent;
using GameServer.Protocol;

namespace GameServer.Matchmaking;

/// <summary>
/// The assignment step ("director" in Open Match terms): runs a pool's tickets through a match
/// function and the evaluator, then turns each winning match into a room (via <see cref="IRoomAllocator"/>)
/// and a per-player join token (via <see cref="IMatchJoinTokenIssuer"/>). It owns the
/// double-assignment guard: a ticket id is claimed atomically and exactly once, so two directors (or
/// two cycles) racing the same winning match can never both assign the same ticket.
/// </summary>
/// <remarks>
/// Race-safety: assignment claims each ticket id into a shared, atomic set
/// (<see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by ticket id) BEFORE allocating a room. If
/// any ticket in a winning match is already claimed, the whole match is skipped — a ticket can never
/// be in two assignments. The evaluator already produces non-overlapping winners within a single
/// cycle; the claim set extends that guarantee ACROSS concurrent cycles/directors sharing the same
/// store. The claim happens before room allocation so a lost race spends no allocation.
///
/// Matchmaking has zero dependency on the room runtime: it only ever calls the two ports, which the
/// Host wires to real allocation + token mint.
/// </remarks>
public sealed class MatchDirector
{
    private readonly IMatchFunction _matchFunction;
    private readonly Evaluator _evaluator;
    private readonly IRoomAllocator _allocator;
    private readonly IMatchJoinTokenIssuer _tokenIssuer;

    // Shared across cycles (and, if shared, across directors): the set of ticket ids already assigned.
    // A ticket id present here has been handed a room+token and must never be assigned again.
    private readonly ConcurrentDictionary<string, byte> _assigned;

    public MatchDirector(
        IMatchFunction matchFunction,
        Evaluator evaluator,
        IRoomAllocator allocator,
        IMatchJoinTokenIssuer tokenIssuer,
        ConcurrentDictionary<string, byte>? assignedTickets = null)
    {
        _matchFunction = matchFunction;
        _evaluator = evaluator;
        _allocator = allocator;
        _tokenIssuer = tokenIssuer;
        _assigned = assignedTickets ?? new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Runs one assignment cycle over <paramref name="pool"/> against <paramref name="tickets"/>:
    /// filters by the pool (scope + predicate), forms candidates, evaluates winners, and assigns each
    /// winner that can claim all its tickets and obtain a room. Tickets the cluster cannot place are
    /// left UNassigned (their ticket-id claims are rolled back) so they retry next cycle — overload is
    /// shed, not silently dropped.
    /// </summary>
    public IReadOnlyList<MatchAssignment> Cycle(Pool pool, IReadOnlyList<MatchTicket> tickets)
    {
        var admitted = pool.Filter(tickets).ToList();
        var candidates = _matchFunction.Form(pool.Scope, admitted);
        var winners = _evaluator.Evaluate(candidates);

        var assignments = new List<MatchAssignment>();

        foreach (var match in winners)
        {
            // Atomically claim every ticket in the match. If ANY ticket is already assigned, abandon the
            // whole match and release the ids we just claimed — a partial/double assignment never happens.
            var claimedNow = new List<string>(match.Tickets.Count);
            var lostRace = false;
            foreach (var ticket in match.Tickets)
            {
                if (_assigned.TryAdd(ticket.TicketId, 0))
                {
                    claimedNow.Add(ticket.TicketId);
                }
                else
                {
                    lostRace = true;
                    break;
                }
            }

            if (lostRace)
            {
                Rollback(claimedNow);
                continue;
            }

            var roomId = _allocator.Allocate(match.Scope);
            if (roomId is null)
            {
                // Cluster at capacity: release the claims so these tickets retry, do not strand them.
                Rollback(claimedNow);
                continue;
            }

            var players = new List<PlayerAssignment>(match.Tickets.Count);
            foreach (var ticket in match.Tickets)
            {
                var token = _tokenIssuer.IssueJoinToken(match.Scope, roomId.Value, ticket.PlayerId);
                players.Add(new PlayerAssignment(ticket.TicketId, ticket.PlayerId, match.Scope, roomId.Value, token));
            }

            assignments.Add(new MatchAssignment(match.Scope, roomId.Value, players));
        }

        return assignments;
    }

    /// <summary>True if <paramref name="ticketId"/> has already been assigned (and so must never be re-assigned).</summary>
    public bool IsAssigned(string ticketId) => _assigned.ContainsKey(ticketId);

    private void Rollback(IEnumerable<string> ticketIds)
    {
        foreach (var id in ticketIds)
        {
            _assigned.TryRemove(id, out _);
        }
    }
}
