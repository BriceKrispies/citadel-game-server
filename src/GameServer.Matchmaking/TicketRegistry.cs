using System.Collections.Concurrent;
using GameServer.Protocol;
using GameServer.Tenancy;

namespace GameServer.Matchmaking;

/// <summary>
/// The in-memory matchmaking frontend: where players submit intent (a ticket) and where the director
/// reads tickets FROM. Tickets are bucketed by <see cref="MatchScope"/>, so a read for one scope can
/// physically never return another scope's tickets — ticket leakage across tenants/games/versions is
/// structurally impossible, not a filter that could be forgotten.
/// </summary>
/// <remarks>
/// Determinism: enqueue time is read through an injected <see cref="IMonotonicClock"/>, never
/// wall-clock, so age-based fairness is reproducible under test. This is the swappable in-memory
/// implementation; a distributed store (Redis-backed, like the room directory) would live behind the
/// same shape in the cluster project and behave identically.
/// </remarks>
public sealed class TicketRegistry
{
    private readonly IMonotonicClock _clock;
    private readonly ConcurrentDictionary<MatchScope, ConcurrentDictionary<string, MatchTicket>> _byScope = new();

    public TicketRegistry(IMonotonicClock clock) => _clock = clock;

    /// <summary>
    /// Submits a ticket for <paramref name="player"/> in <paramref name="scope"/>, stamping it with the
    /// current monotonic time. Returns the stored ticket. The scope is fixed at submit time and is the
    /// only bucket the ticket can ever be read from.
    /// </summary>
    public MatchTicket Submit(
        string ticketId,
        MatchScope scope,
        PlayerId player,
        int skill = 0,
        IReadOnlyDictionary<string, string>? attributes = null)
    {
        var ticket = new MatchTicket(ticketId, scope, player, skill, _clock.ElapsedSeconds, attributes);
        var bucket = _byScope.GetOrAdd(scope, static _ => new ConcurrentDictionary<string, MatchTicket>(StringComparer.Ordinal));
        bucket[ticketId] = ticket;
        return ticket;
    }

    /// <summary>The active tickets in <paramref name="scope"/>. Never returns any other scope's tickets.</summary>
    public IReadOnlyList<MatchTicket> ActiveTickets(MatchScope scope) =>
        _byScope.TryGetValue(scope, out var bucket)
            ? bucket.Values.ToList()
            : Array.Empty<MatchTicket>();

    /// <summary>Removes assigned tickets from <paramref name="scope"/> so they are not re-matched.</summary>
    public void Remove(MatchScope scope, IEnumerable<string> ticketIds)
    {
        if (!_byScope.TryGetValue(scope, out var bucket))
        {
            return;
        }

        foreach (var id in ticketIds)
        {
            bucket.TryRemove(id, out _);
        }
    }
}
