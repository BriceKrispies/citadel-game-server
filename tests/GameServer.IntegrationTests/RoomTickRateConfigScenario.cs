using GameServer.Host;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// The authoritative cadence must be configurable from the host (<c>Realtime:TickHz</c>) so a
/// scenario can drive 30 Hz without recompiling, while the simulation kernel stays wall-clock-free.
/// This boots the real <see cref="Program"/> wiring and asserts the resolved
/// <see cref="RoomTickService"/> reflects the configured rate, plus that the pure Hz→interval
/// conversion guards bad config by falling back to the 10 Hz default. Host-level config wiring is
/// tested here (alongside the other host scenarios) rather than co-located, because the hermetic
/// fast unit suite deliberately does not reference the host.
/// </summary>
public sealed class RoomTickRateConfigScenario
{
    private readonly ITestOutputHelper _output;

    public RoomTickRateConfigScenario(ITestOutputHelper output) => _output = output;

    [Fact]
    public void IntervalForHz_MapsRateToInterval_AndGuardsBadConfig()
    {
        Assert.Equal(100.0, RoomTickService.IntervalForHz(10).TotalMilliseconds, precision: 3);
        Assert.Equal(1000.0 / 30.0, RoomTickService.IntervalForHz(30).TotalMilliseconds, precision: 3);

        // Non-positive / non-finite rates are nonsensical for a cadence; fall back to the default
        // rather than producing a zero/negative/NaN timer interval that would throw or busy-spin.
        var fallback = RoomTickService.IntervalForHz(RoomTickService.DefaultTickHz).TotalMilliseconds;
        Assert.Equal(fallback, RoomTickService.IntervalForHz(0).TotalMilliseconds, precision: 3);
        Assert.Equal(fallback, RoomTickService.IntervalForHz(-5).TotalMilliseconds, precision: 3);
        Assert.Equal(fallback, RoomTickService.IntervalForHz(double.NaN).TotalMilliseconds, precision: 3);
    }

    [Fact]
    public void Host_DefaultsTo10Hz_WhenTickHzUnset()
    {
        using var factory = new WebApplicationFactory<Program>();
        var tick = factory.Services.GetRequiredService<RoomTickService>();

        _output.WriteLine($"default tick interval = {tick.TickInterval.TotalMilliseconds:0.###} ms");
        Assert.Equal(100.0, tick.TickInterval.TotalMilliseconds, precision: 3);
    }

    [Fact]
    public void Host_DrivesConfiguredRate_When30HzRequested()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("Realtime:TickHz", "30"));
        var tick = factory.Services.GetRequiredService<RoomTickService>();

        _output.WriteLine($"30Hz tick interval = {tick.TickInterval.TotalMilliseconds:0.###} ms");
        Assert.Equal(1000.0 / 30.0, tick.TickInterval.TotalMilliseconds, precision: 3);
    }
}
