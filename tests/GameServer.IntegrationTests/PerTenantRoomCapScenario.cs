using GameServer.Protocol;
using GameServer.Simulation;
using GameServer.Transport;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// Gap #11 (tenant fairness) — no per-tenant room ceiling. <see cref="AdmissionPolicy"/> caps the
/// GLOBAL room count (<c>MaxRooms</c>) but not rooms-per-tenant, so one tenant can open rooms until
/// the global ceiling and starve every other tenant out of room capacity — a classic noisy
/// neighbor. This scenario sets <c>MaxRoomsPerTenant = 3</c>, has both tenants try to open 5 rooms,
/// and asserts each is independently capped at 3 (2 shed) — tenant-a exhausting its ceiling never
/// eats into tenant-b's allowance. Room admission enforces the per-tenant ceiling, shedding the
/// overflow with <c>ServerError(Overloaded)</c>.
/// </summary>
public sealed class PerTenantRoomCapScenario
{
    private readonly ITestOutputHelper _output;

    public PerTenantRoomCapScenario(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task OneTenantsRoomFlood_IsCapped_WithoutStarvingAnotherTenant()
    {
        const int capPerTenant = 3;
        const int attempts = 5;

        var harness = new IntegrationHarness(
            _ => new MoveRightGame(),
            tenants: new[] { "tenant-a", "tenant-b" },
            lifecycle: RoomLifecycle.Persist, // keep created rooms counted while we probe the cap
            admission: new AdmissionPolicy(MaxRoomsPerTenant: capPerTenant));

        var (aCreated, aShed) = await OpenRoomsAsync(harness, "tenant-a", attempts);
        var (bCreated, bShed) = await OpenRoomsAsync(harness, "tenant-b", attempts);

        _output.WriteLine($"tenant-a created {aCreated}/{attempts} (shed {aShed}); tenant-b created {bCreated}/{attempts} (shed {bShed})");

        Assert.Equal(capPerTenant, aCreated);
        Assert.Equal(attempts - capPerTenant, aShed);
        // tenant-b gets its OWN independent per-tenant ceiling (also 3): tenant-a exhausting its
        // ceiling never reduced tenant-b's allowance — that isolation is the noisy-neighbour
        // protection. (A shared global cap would have let tenant-a starve tenant-b below 3.)
        Assert.Equal(capPerTenant, bCreated);
        Assert.Equal(attempts - capPerTenant, bShed);
    }

    private static async Task<(int Created, int Shed)> OpenRoomsAsync(IntegrationHarness harness, string tenant, int count)
    {
        var created = 0;
        var shed = 0;
        for (var i = 0; i < count; i++)
        {
            var transport = await harness.RunClientAsync(tenant, $"{tenant}-room-{i}", "p1", "demo", Array.Empty<string>());
            var overloaded = transport.DrainOutbound().Any(m => m.Payload is ServerError { Code: ServerErrorCode.Overloaded });
            if (overloaded)
            {
                shed++;
            }
            else
            {
                created++;
            }
        }

        return (created, shed);
    }
}
