using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameServer.LoadHarness;

/// <summary>How a virtual client obtains its join token.</summary>
public enum JoinTokenMode
{
    /// <summary>Use the static <see cref="ScenarioConfig.JoinToken"/> for every client (custom setups).</summary>
    Static,

    /// <summary>Obtain a token per client from the control plane (POST /api/v1/rooms/{id}/join-token).</summary>
    ControlPlane,
}

/// <summary>When a virtual client acknowledges server ticks.</summary>
public enum SnapshotAckMode
{
    None,
    EverySnapshot,
    EveryNthSnapshot,
}

/// <summary>How a virtual client reacts to disconnects.</summary>
public enum ReconnectMode
{
    None,
    OnUnexpectedClose,
    Periodic,
}

/// <summary>
/// The full, JSON-serializable scenario contract. One config describes one harness
/// instance's share of a (possibly distributed) load test. The schema scales to
/// 50,000 clients by running many instances, each with its own slice of clients.
/// </summary>
public sealed record ScenarioConfig
{
    public string Name { get; init; } = "unnamed";

    /// <summary>One of the supported scenario kinds, e.g. "sustained-input" (kebab-case).</summary>
    public string ScenarioType { get; init; } = "connection-ramp";

    /// <summary>Base server URL, e.g. "http://localhost:5000". WS and HTTP URLs are derived from it.</summary>
    public string ServerUrl { get; init; } = "http://localhost:5000";

    public JoinTokenMode JoinTokenMode { get; init; } = JoinTokenMode.ControlPlane;

    /// <summary>Static token used only when <see cref="JoinTokenMode"/> is Static.</summary>
    public string? JoinToken { get; init; }

    public string TenantId { get; init; } = "tenant-a";
    public string GameId { get; init; } = "demo-game";

    public int RoomCount { get; init; } = 1;
    public int ClientsPerRoom { get; init; } = 1;
    public int TotalClients { get; init; } = 1;

    /// <summary>Seconds over which connections are opened.</summary>
    public double RampUpDuration { get; init; } = 1;

    /// <summary>Seconds of steady-state load after ramp-up.</summary>
    public double SteadyStateDuration { get; init; } = 10;

    public double InputRatePerClientPerSecond { get; init; }

    public SnapshotAckMode SnapshotAckMode { get; init; } = SnapshotAckMode.EverySnapshot;

    /// <summary>Seconds between client pings. Zero disables pings.</summary>
    public double PingInterval { get; init; }

    public ReconnectMode ReconnectMode { get; init; } = ReconnectMode.None;

    /// <summary>Percentage [0,100] of clients that send a malformed frame.</summary>
    public double MalformedClientPercent { get; init; }

    /// <summary>Percentage [0,100] of clients that drain their receive buffer slowly.</summary>
    public double SlowReceiverPercent { get; init; }

    public string OutputDirectory { get; init; } = "load/results";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter() },
    };

    public static ScenarioConfig FromJson(string json) =>
        JsonSerializer.Deserialize<ScenarioConfig>(json, Options)
            ?? throw new InvalidOperationException("Scenario config deserialized to null.");

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>The realtime WebSocket connect URL (http→ws, https→wss).</summary>
    public Uri RealtimeUri(string joinToken)
    {
        var ws = ServerUrl.Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase)
                          .Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase)
                          .TrimEnd('/');
        return new Uri($"{ws}/realtime/v1/connect?joinToken={Uri.EscapeDataString(joinToken)}");
    }

    public string ControlPlaneBaseUrl => ServerUrl.TrimEnd('/');
}
