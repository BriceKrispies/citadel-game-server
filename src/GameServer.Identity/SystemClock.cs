namespace GameServer.Identity;

/// <summary>Production clock backed by the system wall clock (the <see cref="IClock"/> adapter).</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
