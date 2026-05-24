namespace GameServer.Protocol;

/// <summary>
/// Discriminator for the protocol message family. Kept as an explicit enum so
/// routing and codecs never branch on stringly-typed message names.
/// </summary>
public enum MessageType
{
    // Client -> Server
    ClientHello,
    ClientJoinRoom,
    ClientCommand,
    ClientAck,

    // Server -> Client
    ServerWelcome,
    ServerSnapshot,
    ServerError,
}
