using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GameServer.Protocol;

/// <summary>
/// JSON implementation of <see cref="IMessageCodec"/>. Envelope metadata is
/// written with flat, stable field names; the payload is serialized by its
/// declared <see cref="MessageType"/> so decoding is never ambiguous.
/// </summary>
public sealed class JsonMessageCodec : IMessageCodec
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public string Encode(MessageEnvelope envelope)
    {
        if (!envelope.IsConsistent)
        {
            throw new MessageCodecException(
                $"Envelope declares {envelope.MessageType} but payload is {envelope.Payload.Type}.");
        }

        var obj = new JsonObject
        {
            ["tenantId"] = envelope.TenantId.Value,
            ["gameId"] = envelope.GameId.Value,
            ["roomId"] = envelope.RoomId?.Value,
            ["sessionId"] = envelope.SessionId?.Value,
            ["playerId"] = envelope.PlayerId?.Value,
            ["protocolVersion"] = envelope.ProtocolVersion,
            ["messageType"] = envelope.MessageType.ToString(),
            ["sequence"] = envelope.Sequence,
            ["traceId"] = envelope.TraceId,
            ["payload"] = JsonSerializer.SerializeToNode(
                envelope.Payload, envelope.Payload.GetType(), PayloadOptions),
        };

        return obj.ToJsonString();
    }

    public MessageEnvelope Decode(string wire)
    {
        JsonNode root;
        try
        {
            root = JsonNode.Parse(wire) ?? throw new MessageCodecException("Wire payload is null.");
        }
        catch (JsonException ex)
        {
            throw new MessageCodecException($"Wire payload is not valid JSON: {ex.Message}");
        }

        var messageType = ParseMessageType(RequireString(root, "messageType"));
        var payloadNode = root["payload"]
            ?? throw new MessageCodecException("Envelope is missing a payload.");

        IMessagePayload payload = messageType switch
        {
            MessageType.ClientHello => Deserialize<ClientHello>(payloadNode),
            MessageType.ClientJoinRoom => Deserialize<ClientJoinRoom>(payloadNode),
            MessageType.ClientCommand => Deserialize<ClientCommand>(payloadNode),
            MessageType.ServerWelcome => Deserialize<ServerWelcome>(payloadNode),
            MessageType.ServerSnapshot => Deserialize<ServerSnapshot>(payloadNode),
            MessageType.ServerError => Deserialize<ServerError>(payloadNode),
            _ => throw new MessageCodecException($"Unknown message type '{messageType}'."),
        };

        var envelope = new MessageEnvelope
        {
            TenantId = new TenantId(RequireString(root, "tenantId")),
            GameId = new GameId(RequireString(root, "gameId")),
            RoomId = OptionalString(root, "roomId") is { } r ? new RoomId(r) : null,
            SessionId = OptionalString(root, "sessionId") is { } s ? new SessionId(s) : null,
            PlayerId = OptionalString(root, "playerId") is { } p ? new PlayerId(p) : null,
            ProtocolVersion = (int)RequireNumber(root, "protocolVersion"),
            MessageType = messageType,
            Sequence = (long)RequireNumber(root, "sequence"),
            TraceId = RequireString(root, "traceId"),
            Payload = payload,
        };

        if (!envelope.IsConsistent)
        {
            throw new MessageCodecException("Decoded payload type does not match declared message type.");
        }

        return envelope;
    }

    private static T Deserialize<T>(JsonNode node) where T : IMessagePayload
    {
        var value = node.Deserialize<T>(PayloadOptions);
        return value ?? throw new MessageCodecException($"Payload could not be decoded as {typeof(T).Name}.");
    }

    private static MessageType ParseMessageType(string value) =>
        Enum.TryParse<MessageType>(value, ignoreCase: false, out var parsed)
            ? parsed
            : throw new MessageCodecException($"Unknown message type '{value}'.");

    private static string RequireString(JsonNode root, string name) =>
        OptionalString(root, name) ?? throw new MessageCodecException($"Missing required field '{name}'.");

    private static string? OptionalString(JsonNode root, string name)
    {
        var node = root[name];
        return node is null ? null : node.GetValue<string>();
    }

    private static double RequireNumber(JsonNode root, string name)
    {
        var node = root[name] ?? throw new MessageCodecException($"Missing required field '{name}'.");
        return node.GetValue<double>();
    }
}
