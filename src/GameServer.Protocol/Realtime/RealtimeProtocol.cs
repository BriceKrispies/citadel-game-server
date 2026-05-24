using GameServer.Protocol.Realtime.V1;

namespace GameServer.Protocol.Realtime;

/// <summary>
/// Version policy for the canonical realtime protocol. The wire contract lives in
/// <c>contracts/realtime/proto/gameserver.realtime.v1.proto</c>; this is the
/// server's view of which versions it accepts.
/// </summary>
public static class RealtimeProtocol
{
    /// <summary>The single protocol version this build speaks.</summary>
    public const ProtocolVersion SupportedVersion = ProtocolVersion.V1;

    public static bool IsSupported(ProtocolVersion version) => version == SupportedVersion;
}
