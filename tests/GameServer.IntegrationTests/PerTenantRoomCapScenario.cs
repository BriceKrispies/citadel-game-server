using GameServer.Protocol;
using GameServer.Simulation;
using GameServer.Transport;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// Gap #11 (tenant fairness) — no per-tenant room ceiling. <see cref="AdmissionPolicy"/> caps the
/// GLOBAL room count (<c>MaxRooms</c>) but not rooms-per-tenant, so one tenant can open rooms until
/// the global ceiling and starve every other tenant out of room capacity — a classic noisy
/// neighbor. This scenario sets <c>MaxRoomsPerTenant = 3</c>, has tenant-a try to open 5 rooms and
/// tenant-b open 5, and asserts tenant-a is capped at 3 (2 shed) while tenant-b is unaffected. It
/// FAILS today (the edge ignores <c>MaxRoomsPerTenant</c>) and turns green once room admission
/// enforces a per-tenant ceiling, shedding the overflow with <c>ServerError(Overloaded)</c>.
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
        var (bCreated, _) = await OpenRoomsAsync(harness, "tenant-b", attempts);

        _output.WriteLine($"tenant-a created {aCreated}/{attempts} (shed {aShed}); tenant-b created {bCreated}/{attempts}");

        Assert.Equal(capPerTenant, aCreated); // RED today: no per-tenant room enforcement -> all 5 created
        Assert.Equal(attempts - capPerTenant, aShed);
        Assert.Equal(attempts, bCreated); // tenant-b must be untouched by tenant-a's flood
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
