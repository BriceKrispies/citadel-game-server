// Distributed load harness for the Citadel realtime server. Speaks the real binary
// protobuf WebSocket protocol. Run one instance locally, or many instances across
// load-generator machines to reach 50k clients. See LOAD_TESTING.md.
//
// Usage: dotnet run --project src/GameServer.LoadHarness -- <path-to-scenario.json>

using GameServer.LoadHarness;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: GameServer.LoadHarness <scenario-config.json>");
    return 2;
}

var configPath = args[0];
if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"Scenario config not found: {configPath}");
    return 2;
}

var config = ScenarioConfig.FromJson(await File.ReadAllTextAsync(configPath));

using var http = new HttpClient();
// Control-plane calls (create-room, mint-token, admin observe) require an authenticated
// caller; the /api/v1 group answers 401 otherwise. The key must be authorized for the
// scenario's tenant. Static-token scenarios skip the control plane and need no key.
if (!string.IsNullOrEmpty(config.ApiKey))
{
    http.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.ApiKey);
}
else if (config.JoinTokenMode == JoinTokenMode.ControlPlane)
{
    Console.Error.WriteLine("Warning: joinTokenMode=ControlPlane but no apiKey set; control-plane calls will be rejected (401).");
}

var clock = new SystemLoadClock();
var metrics = new MetricsRecorder();
var failures = new FailureRecorder();
var exporter = new ResultExporter();
var tokens = new JoinTokenProvider(http, config);
// Server-side per-room verification only applies when the control plane is in play.
var verifier = config.JoinTokenMode == JoinTokenMode.ControlPlane ? new ServerRoomVerifier(http, config) : null;

var runner = new ScenarioRunner(
    config,
    () => new WebSocketConnection(),
    tokens,
    metrics,
    failures,
    exporter,
    clock,
    Console.Out,
    verifier);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    await runner.RunAsync(cts.Token);
    Console.WriteLine($"Results written to: {config.OutputDirectory}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Load run failed: {ex.Message}");
    return 1;
}
