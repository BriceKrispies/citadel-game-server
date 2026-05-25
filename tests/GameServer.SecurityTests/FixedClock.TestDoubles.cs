using GameServer.Identity;

namespace GameServer.SecurityTests;

/// <summary>
/// A frozen <see cref="IClock"/> so the token codec issues a token stamped at a chosen instant
/// (e.g. far in the past, to mint an already-expired token) without any real waiting.
/// </summary>
internal sealed class FixedClock : IClock
{
    public FixedClock(DateTimeOffset now) => UtcNow = now;

    public DateTimeOffset UtcNow { get; }
}
