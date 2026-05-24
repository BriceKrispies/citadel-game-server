using Xunit;

namespace GameServer.LoadHarness;

public sealed class ScenarioConfigTests
{
    [Fact]
    public void ScenarioConfig_ParsesValidConfig()
    {
        const string json = """
        {
          "name": "smoke",
          "scenarioType": "sustained-input",
          "serverUrl": "http://localhost:5000",
          "joinTokenMode": "ControlPlane",
          "tenantId": "tenant-a",
          "gameId": "demo-game",
          "roomCount": 10,
          "clientsPerRoom": 10,
          "totalClients": 100,
          "rampUpDuration": 10,
          "steadyStateDuration": 60,
          "inputRatePerClientPerSecond": 2,
          "snapshotAckMode": "EverySnapshot",
          "pingInterval": 5,
          "reconnectMode": "None",
          "malformedClientPercent": 0,
          "slowReceiverPercent": 0,
          "outputDirectory": "load/results/smoke"
        }
        """;

        var config = ScenarioConfig.FromJson(json);

        Assert.Equal("smoke", config.Name);
        Assert.Equal("sustained-input", config.ScenarioType);
        Assert.Equal(JoinTokenMode.ControlPlane, config.JoinTokenMode);
        Assert.Equal("tenant-a", config.TenantId);
        Assert.Equal(100, config.TotalClients);
        Assert.Equal(10, config.RoomCount);
        Assert.Equal(2, config.InputRatePerClientPerSecond);
        Assert.Equal(SnapshotAckMode.EverySnapshot, config.SnapshotAckMode);
        Assert.Equal(60, config.SteadyStateDuration);
        Assert.Equal(ReconnectMode.None, config.ReconnectMode);
        Assert.Equal("load/results/smoke", config.OutputDirectory);
    }
}
