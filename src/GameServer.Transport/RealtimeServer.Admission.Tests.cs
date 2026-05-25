using GameServer.Transport.Testing;
using Xunit;

namespace GameServer.Transport;

/// <summary>
/// The runtime-mutable admission ceilings (the limits-config control-plane endpoint): a platform admin can
/// re-tighten/loosen capacity without a redeploy, and the change is reflected immediately.
/// </summary>
public sealed class RealtimeServerAdmissionTests
{
    [Fact]
    public void UpdateAdmission_ReplacesCeilings_AndReadsBackThroughTheGetter()
    {
        var harness = new SliceHarness("tenant-a");
        var initial = new AdmissionPolicy(MaxConnections: 10, MaxRooms: 5);
        harness.Server.UpdateAdmission(initial);
        Assert.Equal(10, harness.Server.Admission.MaxConnections);
        Assert.Equal(5, harness.Server.Admission.MaxRooms);

        harness.Server.UpdateAdmission(initial with { MaxRooms = 50 });

        Assert.Equal(50, harness.Server.Admission.MaxRooms);
        Assert.Equal(10, harness.Server.Admission.MaxConnections); // unchanged
    }
}
