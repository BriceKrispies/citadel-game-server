namespace GameServer.Matchmaking;

/// <summary>
/// Resolves overlapping candidate matches into a set of non-overlapping WINNERS. Two candidate matches
/// "overlap" if they share a ticket; only one of them can win, because a ticket can be in at most one
/// real match. This is the second line of defense against double-assignment (the director's atomic
/// claim is the first): a single ticket never appears in two winning matches.
/// </summary>
/// <remarks>
/// Selection is greedy by descending quality, with ticket-id as a deterministic tie-break — never
/// wall-clock or hash order — so the same candidate set always resolves to the same winners (replay
/// safe). Once a candidate wins, every later candidate sharing any of its tickets is dropped.
/// </remarks>
public sealed class Evaluator
{
    public IReadOnlyList<CandidateMatch> Evaluate(IReadOnlyList<CandidateMatch> candidates)
    {
        var ordered = candidates
            .OrderByDescending(c => c.Quality)
            .ThenBy(c => string.Join(",", c.TicketIds.OrderBy(id => id, StringComparer.Ordinal)), StringComparer.Ordinal)
            .ToList();

        var winners = new List<CandidateMatch>();
        var claimed = new HashSet<string>();

        foreach (var candidate in ordered)
        {
            if (candidate.TicketIds.Any(claimed.Contains))
            {
                continue; // Overlaps an already-chosen (higher-quality) match; drop it.
            }

            foreach (var id in candidate.TicketIds)
            {
                claimed.Add(id);
            }

            winners.Add(candidate);
        }

        return winners;
    }
}
