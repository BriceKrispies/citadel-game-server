using System.Reflection;
using Google.Protobuf;
using Xunit;
using V1 = GameServer.Protocol.Realtime.V1;

namespace GameServer.Protocol.Realtime;

/// <summary>
/// Test #2 (GoldenPacketRoundTrip): a byte-level contract pin. Each canonical envelope encodes
/// to EXACTLY the bytes committed under <c>contracts/realtime/fixtures/*.bin</c>, and those bytes
/// decode back to an equal envelope. Browser clients are built against this wire shape, so a
/// silent change to the protobuf encoding (a renumbered field, a changed type, a dropped value)
/// must break this test rather than ship.
///
/// The fixtures are the committed contract. They are generated from the codec on first run (when
/// absent) and then pinned; thereafter any drift in the encoded bytes fails the test. To
/// intentionally re-pin after an additive, reviewed change, delete the affected fixture and re-run.
/// </summary>
public sealed class GoldenPacketTests
{
    private readonly RealtimeProtobufCodec _codec = new();

    // Every canonical envelope shares this correlation header so the fixtures differ only in payload.
    private static V1.RealtimeEnvelope Base(V1.MessageType type) => new()
    {
        ProtocolVersion = V1.ProtocolVersion.V1,
        MessageId = "msg-1",
        MessageType = type,
        TenantId = "tenant-a",
        GameId = "demo-game",
        RoomId = "room-1",
        SessionId = "session-1",
        PlayerId = "player-1",
        ConnectionId = "conn-1",
        Sequence = 7,
        Ack = 3,
        ClientTick = 11,
        ServerTick = 99,
        TraceId = "trace-1",
        LogicalChannel = V1.LogicalChannel.Gameplay,
    };

    private static V1.EntityState Entity(string id, params byte[] payload) =>
        new() { EntityId = id, Payload = ByteString.CopyFrom(payload) };

    public static IEnumerable<object[]> Canonical()
    {
        yield return Fixture("hello", V1.MessageType.ClientHello, e => e.ClientHello = new V1.ClientHello
        {
            RequestedProtocolVersion = V1.ProtocolVersion.V1,
            JoinToken = "join-token-1",
            ClientName = "Alice",
        });

        yield return Fixture("welcome", V1.MessageType.ServerWelcome, e => e.ServerWelcome = new V1.ServerWelcome
        {
            AcceptedProtocolVersion = V1.ProtocolVersion.V1,
            SessionId = "session-1",
            ConnectionId = "conn-1",
            ServerTick = 99,
        });

        yield return Fixture("join", V1.MessageType.ClientJoinRoom, e => e.ClientJoinRoom = new V1.ClientJoinRoom { RoomId = "room-1" });

        yield return Fixture("leave", V1.MessageType.ClientLeaveRoom, e => e.ClientLeaveRoom = new V1.ClientLeaveRoom { RoomId = "room-1" });

        yield return Fixture("snapshot", V1.MessageType.ServerSnapshot, e =>
        {
            var s = new V1.ServerSnapshot { ServerTick = 99 };
            s.Entities.Add(Entity("player-1", 1, 0, 0, 0));
            s.Entities.Add(Entity("player-2", 5, 0, 0, 0));
            e.ServerSnapshot = s;
        });

        yield return Fixture("delta", V1.MessageType.ServerDelta, e =>
        {
            var d = new V1.ServerDelta { FromServerTick = 90, ToServerTick = 99 };
            d.ChangedEntities.Add(Entity("player-1", 2, 0, 0, 0));
            d.RemovedEntities.Add("player-9");
            e.ServerDelta = d;
        });

        yield return Fixture("correction", V1.MessageType.ServerCorrection, e => e.ServerCorrection = new V1.ServerCorrection
        {
            ServerTick = 99,
            AckedClientTick = 11,
            AuthoritativeEntity = Entity("player-1", 7, 0, 0, 0),
        });

        yield return Fixture("event", V1.MessageType.ServerEvent, e => e.ServerEvent = new V1.ServerEvent
        {
            EventType = "player_joined",
            Payload = ByteString.CopyFromUtf8("player-1"),
        });

        yield return Fixture("error", V1.MessageType.ServerError, e => e.ServerError = new V1.ServerError
        {
            Code = V1.ErrorCode.PlayerNotInRoom,
            Message = "not in room",
            Fatal = false,
            DisconnectReason = V1.DisconnectReason.Unspecified,
        });
    }

    private static object[] Fixture(string name, V1.MessageType type, Action<V1.RealtimeEnvelope> set)
    {
        var env = Base(type);
        set(env);
        return new object[] { name, env };
    }

    [Theory]
    [MemberData(nameof(Canonical))]
    public void GoldenPacketRoundTrip(string name, V1.RealtimeEnvelope canonical)
    {
        var encoded = _codec.Encode(canonical);

        var path = Path.Combine(FixtureDir(), $"{name}.bin");
        if (!File.Exists(path))
        {
            // First run: pin the canonical bytes as the committed contract.
            Directory.CreateDirectory(FixtureDir());
            File.WriteAllBytes(path, encoded);
        }

        var pinned = File.ReadAllBytes(path);

        // Encoding must match the committed bytes EXACTLY (no silent wire drift).
        Assert.Equal(pinned, encoded);

        // And the committed bytes must decode back to the exact canonical envelope.
        var decoded = _codec.Decode(pinned);
        Assert.Equal(canonical, decoded);
    }

    /// <summary>Locates <c>contracts/realtime/fixtures</c> by walking up from the test assembly.</summary>
    private static string FixtureDir()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "contracts", "realtime")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate the repository's contracts/realtime directory.");
        }

        return Path.Combine(dir.FullName, "contracts", "realtime", "fixtures");
    }
}
