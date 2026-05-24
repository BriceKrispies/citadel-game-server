namespace GameServer.Replication;

/// <summary>Opaque identity of a replicated entity (a player, projectile, item, …).</summary>
public readonly record struct EntityId(string Value);

/// <summary>Opaque identity of a viewer (a connection/player receiving state).</summary>
public readonly record struct ViewerId(string Value);

/// <summary>
/// Generic relevance key. A game maps its own notion of "where/what" onto these
/// fields; the platform uses them for interest filtering but never interprets the
/// entity payload. <see cref="X"/>/<see cref="Y"/> support spatial strategies
/// (radius, grid); <see cref="Group"/> supports team/zone/subscription strategies.
/// </summary>
public readonly record struct RelevanceKey(double X, double Y, string Group)
{
    public static readonly RelevanceKey None = new(0, 0, string.Empty);

    public double DistanceTo(RelevanceKey other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}

/// <summary>
/// One entity's authoritative state for a tick: identity, monotonic version (for
/// delta detection), relevance key (for interest), and an opaque payload (the wire
/// bytes the game produced; the platform only measures its size).
/// </summary>
public sealed record EntitySnapshot(EntityId Id, long Version, RelevanceKey Key, byte[] Payload)
{
    public int SizeBytes => Payload.Length;
}

/// <summary>A viewer and its own relevance key (e.g. its position/team) for interest filtering.</summary>
public sealed record Viewer(ViewerId Id, RelevanceKey Key);
