namespace GameServer.LoadHarness.Testing;

/// <summary>
/// Deterministic clock for tests. Time advances only when <see cref="Advance"/> is
/// called, so latency math is reproducible and free of wall-clock time. Timestamp
/// units are milliseconds.
/// </summary>
public sealed class FakeLoadClock : ILoadClock
{
    private long _nowMs;

    public long GetTimestamp() => _nowMs;

    public TimeSpan GetElapsed(long startTimestamp) => TimeSpan.FromMilliseconds(_nowMs - startTimestamp);

    public void Advance(TimeSpan delta) => _nowMs += (long)delta.TotalMilliseconds;
}
