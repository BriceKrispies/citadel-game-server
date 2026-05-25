using Xunit;

namespace GameServer.Observability;

/// <summary>
/// Contract for the <see cref="IReadinessCheck"/> seam and its <see cref="ReadinessResult"/> value:
/// a contributor names its dependency and reports ready/not-ready with a detail. Pinned through a
/// fake so the aggregation logic at the host's <c>/ready</c> endpoint has a stable shape to fold.
/// </summary>
public sealed class ReadinessCheckContractTests
{
    [Fact]
    public void Healthy_Result_IsReady_WithName()
    {
        var result = ReadinessResult.Healthy("telemetry");
        Assert.Equal("telemetry", result.Name);
        Assert.True(result.Ready);
    }

    [Fact]
    public void Unhealthy_Result_CarriesTheReason()
    {
        var result = ReadinessResult.Unhealthy("snapshot-store", "store is unreachable");
        Assert.Equal("snapshot-store", result.Name);
        Assert.False(result.Ready);
        Assert.Equal("store is unreachable", result.Detail);
    }

    [Fact]
    public void Check_ReportsTheConfiguredOutcome()
    {
        IReadinessCheck up = new FakeCheck("a", ready: true);
        IReadinessCheck down = new FakeCheck("b", ready: false);

        Assert.True(up.Check().Ready);
        Assert.False(down.Check().Ready);
        Assert.Equal("b", down.Check().Name);
    }

    private sealed class FakeCheck : IReadinessCheck
    {
        private readonly bool _ready;
        public FakeCheck(string name, bool ready)
        {
            Name = name;
            _ready = ready;
        }

        public string Name { get; }

        public ReadinessResult Check() =>
            _ready ? ReadinessResult.Healthy(Name) : ReadinessResult.Unhealthy(Name, "down");
    }
}
