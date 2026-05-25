using System.Collections.Concurrent;
using GameServer.Tenancy;

namespace GameServer.Matchmaking;

/// <summary>
/// Retains per-ticket match assignments so a player whose ticket was QUEUED on submit and matched by a
/// LATER cycle (driven by some other player's submission) can still fetch its room + join token, instead
/// of polling a <c>202 Location</c> that 404s forever and being stranded after the ticket is removed from
/// the registry. Bounded by a TTL: entries older than the configured window are pruned, so the store
/// cannot grow without limit under sustained traffic. Enqueue/lookup times are read through an injected
/// <see cref="IMonotonicClock"/>, never wall-clock, so retention is deterministic under test.
/// </summary>
/// <remarks>
/// This is the in-memory implementation, scoped to a single node — the same shape a distributed
/// (Redis-backed) store would take in the cluster project. An assignment is recorded exactly once per
/// ticket id (assignments are immutable and ticket ids are unique per submit), so a recorded assignment
/// is never overwritten.
/// </remarks>
public sealed class AssignmentStore
{
    private readonly IMonotonicClock _clock;
    private readonly double _ttlSeconds;
    private readonly ConcurrentDictionary<string, Entry> _byTicket = new(StringComparer.Ordinal);

    /// <param name="clock">Monotonic time source for recording and pruning (deterministic in tests).</param>
    /// <param name="ttlSeconds">How long a recorded assignment remains fetchable before it is pruned.</param>
    public AssignmentStore(IMonotonicClock clock, double ttlSeconds = 300)
    {
        if (ttlSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ttlSeconds), ttlSeconds, "TTL must be positive.");
        }

        _clock = clock;
        _ttlSeconds = ttlSeconds;
    }

    /// <summary>The number of assignments currently retained (after the most recent prune). A capacity gauge.</summary>
    public int Count => _byTicket.Count;

    /// <summary>
    /// Records an assignment so it can later be fetched by ticket id. Pruning of expired entries runs on
    /// each record so the store self-bounds without a background timer. Recording the same ticket twice is
    /// a no-op (the first assignment stands — assignments are immutable).
    /// </summary>
    public void Record(PlayerAssignment assignment)
    {
        PruneExpired();
        _byTicket.TryAdd(assignment.TicketId, new Entry(assignment, _clock.ElapsedSeconds));
    }

    /// <summary>
    /// Returns the assignment for <paramref name="ticketId"/> if one was recorded and has not expired.
    /// An expired entry is treated as absent (and removed), so a stale Location does not resurrect.
    /// </summary>
    public bool TryGet(string ticketId, out PlayerAssignment assignment)
    {
        assignment = null!;
        if (!_byTicket.TryGetValue(ticketId, out var entry))
        {
            return false;
        }

        if (IsExpired(entry))
        {
            _byTicket.TryRemove(ticketId, out _);
            return false;
        }

        assignment = entry.Assignment;
        return true;
    }

    /// <summary>Drops every entry older than the TTL. Called on each record; exposed for tests.</summary>
    public void PruneExpired()
    {
        foreach (var pair in _byTicket)
        {
            if (IsExpired(pair.Value))
            {
                _byTicket.TryRemove(pair.Key, out _);
            }
        }
    }

    private bool IsExpired(Entry entry) => _clock.ElapsedSeconds - entry.RecordedAtSeconds > _ttlSeconds;

    private readonly record struct Entry(PlayerAssignment Assignment, double RecordedAtSeconds);
}
