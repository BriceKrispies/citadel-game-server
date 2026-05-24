namespace GameServer.Identity.Testing;

/// <summary>
/// Deterministic <see cref="IClock"/> for tests: time only moves when the test moves
/// it, so token issuance/expiry can be exercised without any real waiting.
/// </summary>
public sealed class FakeClock : IClock
{
    public FakeClock(DateTimeOffset start) => UtcNow = start;

    public DateTimeOffset UtcNow { get; private set; }

    public void Advance(TimeSpan by) => UtcNow += by;

    public void Set(DateTimeOffset to) => UtcNow = to;
}
