namespace GameServer.Persistence;

/// <summary>
/// An event log that supports replaying a bounded tick RANGE and forking the timeline — the rewind
/// seam on the event side. <see cref="ReadRange"/> reads only the events needed to replay from a
/// checkpoint up to a target tick (so a large log is never fully materialized), and
/// <see cref="DiscardAfter"/> drops the now-invalid future when a room is rewound and resumes on a
/// new timeline. It is the mirror of <see cref="IEventLog{TKey,TEvent}.TruncateThrough"/> (which
/// drops the PAST); together they bound the log on both ends.
/// </summary>
public interface IRewindableEventLog<TKey, TEvent> : IEventLog<TKey, TEvent>
    where TKey : notnull
{
    /// <summary>
    /// Events whose sequence is strictly greater than <paramref name="fromExclusive"/> and at or below
    /// <paramref name="toInclusive"/>, in append order. The half-open low bound lets a caller pass the
    /// base checkpoint's tick directly (its folded events are excluded) and the target tick as the
    /// inclusive high bound. Returns empty when the range selects nothing.
    /// </summary>
    IReadOnlyList<TEvent> ReadRange(TKey key, long fromExclusive, long toInclusive);

    /// <summary>
    /// Drops events whose sequence is strictly greater than <paramref name="tick"/> — the future a
    /// rewind to <paramref name="tick"/> invalidates. After it the log ends at the rewind point and
    /// subsequently appended events extend a fresh timeline.
    /// </summary>
    void DiscardAfter(TKey key, long tick);
}
