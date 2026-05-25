using System.Net;
using GameServer.Observability;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// Test #8 (readiness proves dependencies). The old <c>/ready</c> was a near-static literal that
/// said "ready" regardless of whether the node's dependencies could do any work — a fake health
/// check. This boots the real host and proves <c>/ready</c> now runs real dependency probes
/// (<see cref="IReadinessCheck"/> contributors): it is 200 when all dependencies are up, and flips
/// to 503 the moment ANY dependency is down (injected via a failing fake contributor), while
/// <c>/health</c> stays a cheap 200 liveness signal.
/// </summary>
/// <remarks>Boots the real Program.cs wiring via <see cref="WebApplicationFactory{TEntryPoint}"/>.</remarks>
public sealed class ReadinessProbeScenario
{
    private readonly ITestOutputHelper _output;

    public ReadinessProbeScenario(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Ready_Is200_WhenAllDependenciesAreUp()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        var ready = await client.GetAsync("/ready");
        var health = await client.GetAsync("/health");

        _output.WriteLine($"/ready={(int)ready.StatusCode} /health={(int)health.StatusCode}");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Contains("ready", await ready.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ready_Flips503_WhenADependencyIsDown()
    {
        // Inject one failing readiness contributor — modelling a downed dependency (telemetry sink /
        // tenant resolver / snapshot store outage). The real probes stay registered; this one is
        // ADDED, so the endpoint must fold it in and report not-ready.
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton<IReadinessCheck>(new FailingReadinessCheck("snapshot-store"))));
        var client = factory.CreateClient();

        var ready = await client.GetAsync("/ready");
        var health = await client.GetAsync("/health");
        var body = await ready.Content.ReadAsStringAsync();

        _output.WriteLine($"/ready={(int)ready.StatusCode} body={body}");

        // Readiness flips to 503 and names the downed dependency...
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Contains("not-ready", body);
        Assert.Contains("snapshot-store", body);
        // ...but liveness is unaffected: the process is still up.
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task Ready_TreatsAThrowingProbe_AsNotReady()
    {
        // Fail-closed: a contributor that throws must not crash the probe; it counts as not-ready.
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton<IReadinessCheck>(new ThrowingReadinessCheck("tenant-resolver"))));
        var client = factory.CreateClient();

        var ready = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
    }

    private sealed class FailingReadinessCheck : IReadinessCheck
    {
        public FailingReadinessCheck(string name) => Name = name;
        public string Name { get; }
        public ReadinessResult Check() => ReadinessResult.Unhealthy(Name, "injected outage");
    }

    private sealed class ThrowingReadinessCheck : IReadinessCheck
    {
        public ThrowingReadinessCheck(string name) => Name = name;
        public string Name { get; }
        public ReadinessResult Check() => throw new InvalidOperationException("dependency wedged");
    }
}
