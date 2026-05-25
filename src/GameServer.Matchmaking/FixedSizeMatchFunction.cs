namespace GameServer.Matchmaking;

/// <summary>
/// A deterministic match function that forms fixed-size matches from tickets within a single skill
/// band, oldest tickets first. "Oldest first" is the anti-starvation property: a ticket that has
/// waited longest is always at the front of the queue, so no ticket (and therefore no tenant or skill
/// bucket whose tickets are present) can be perpetually skipped while newer tickets are matched.
/// </summary>
/// <remarks>
/// Determinism: tickets are ordered by (enqueued time, then ticket id) — never by hash or wall-clock —
/// so the same input always yields the same matches, which is what the replay/test discipline needs.
/// Skill banding keeps a match's spread bounded (<see cref="_skillBandWidth"/>); a band that cannot
/// fill a full match yields no candidate (those tickets wait, they are not force-matched across a wide
/// skill gap). Because the pool is scope-bound, every candidate is single-scope by construction.
/// </remarks>
public sealed class FixedSizeMatchFunction : IMatchFunction
{
    private readonly int _matchSize;
    private readonly int _skillBandWidth;

    public FixedSizeMatchFunction(int matchSize, int skillBandWidth = int.MaxValue)
    {
        if (matchSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(matchSize), matchSize, "Match size must be at least 1.");
        }

        if (skillBandWidth < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(skillBandWidth), skillBandWidth, "Skill band width must be at least 1.");
        }

        _matchSize = matchSize;
        _skillBandWidth = skillBandWidth;
    }

    public IReadOnlyList<CandidateMatch> Form(MatchScope scope, IReadOnlyList<MatchTicket> tickets)
    {
        // Oldest-first is the fairness ordering: the longest-waiting ticket leads, so nothing starves.
        var queue = tickets
            .Where(t => t.Scope == scope)
            .OrderBy(t => t.EnqueuedSeconds)
            .ThenBy(t => t.TicketId, StringComparer.Ordinal)
            .ToList();

        var candidates = new List<CandidateMatch>();
        var taken = new HashSet<string>();

        foreach (var anchor in queue)
        {
            if (taken.Contains(anchor.TicketId))
            {
                continue;
            }

            // Pull the next oldest tickets that fall within the anchor's skill band.
            var party = new List<MatchTicket> { anchor };
            foreach (var candidate in queue)
            {
                if (party.Count == _matchSize)
                {
                    break;
                }

                if (taken.Contains(candidate.TicketId) || ReferenceEquals(candidate, anchor))
                {
                    continue;
                }

                if (Math.Abs(candidate.Skill - anchor.Skill) < _skillBandWidth)
                {
                    party.Add(candidate);
                }
            }

            if (party.Count != _matchSize)
            {
                continue; // Not enough compatible tickets yet; they wait rather than form a bad match.
            }

            foreach (var t in party)
            {
                taken.Add(t.TicketId);
            }

            // Quality favors a tight skill spread (smaller spread => higher quality) so the evaluator
            // can prefer the better of two overlapping candidates.
            var spread = party.Max(t => t.Skill) - party.Min(t => t.Skill);
            candidates.Add(new CandidateMatch(scope, party, quality: -spread));
        }

        return candidates;
    }
}
