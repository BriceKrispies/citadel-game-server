namespace GameServer.Protocol;

/// <summary>
/// Protocol version policy. The server advertises a single supported version for
/// this slice; negotiation is a membership check against <see cref="Supported"/>.
/// </summary>
public static class ProtocolVersions
{
    /// <summary>The version this build speaks and stamps on server messages.</summary>
    public const int Current = 1;

    /// <summary>Versions the server is willing to accept from clients.</summary>
    public static readonly IReadOnlySet<int> Supported = new HashSet<int> { Current };

    public static bool IsSupported(int version) => Supported.Contains(version);
}
