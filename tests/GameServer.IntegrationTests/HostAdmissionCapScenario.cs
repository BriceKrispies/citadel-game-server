using GameServer.Transport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// Gap #17 (wiring) — the real host runs with admission control switched off. All the admission
/// machinery exists (<see cref="AdmissionPolicy"/>, per-connection / per-tenant / per-room
/// ceilings, explicit shedding), but <c>Program.cs</c> constructs the <see cref="RealtimeServer"/>
/// with <c>admission: null</c> — i.e. <see cref="AdmissionPolicy.Unlimited"/>. So in the deployed
/// process a burst is accepted without bound, which is exactly how a server falls over instead of
/// shedding. This scenario boots the real host and asserts the live server enforces a finite
/// connection ceiling. It FAILS today (unlimited) and turns green once the host configures real
/// ceilings (ideally from configuration).
/// </summary>
/// <remarks>Boots the real Program.cs wiring via <see cref="WebApplicationFactory{TEntryPoint}"/>.</remarks>
public sealed class HostAdmissionCapScenario
{
    private readonly ITestOutputHelper _output;

    public HostAdmissionCapScenario(ITestOutputHelper output) => _output = output;

    [Fact]
    public void DeployedHost_EnforcesAFiniteConnectionCeiling()
    {
        using var factory = new WebApplicationFactory<Program>();
        var server = factory.Services.GetRequiredService<RealtimeServer>();
        var admission = server.Admission;

        _output.WriteLine(
            $"host admission policy: MaxConnections={admission.MaxConnections}, " +
            $"MaxConnectionsPerTenant={admission.MaxConnectionsPerTenant}, MaxRooms={admission.MaxRooms}");

        Assert.NotEqual(int.MaxValue, admission.MaxConnections); // RED today: Program.cs passes admission: null (Unlimited)
        Assert.NotEqual(int.MaxValue, admission.MaxConnectionsPerTenant);
    }
}
