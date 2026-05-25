using Xunit;

namespace GameServer.ControlPlane;

public sealed class CallerPrincipalTests
{
    private static CallerPrincipal Caller(string tenant, params string[] roles) =>
        new("caller-1", tenant, new HashSet<string>(roles));

    [Fact]
    public void CanActFor_OwnTenant_IsAllowed()
    {
        Assert.True(Caller("tenant-a").CanActFor("tenant-a"));
    }

    [Fact]
    public void CanActFor_OtherTenant_IsDenied()
    {
        Assert.False(Caller("tenant-a").CanActFor("tenant-b"));
    }

    [Fact]
    public void CanActFor_PlatformAdmin_MayActForAnyTenant()
    {
        var admin = Caller("tenant-a", CallerPrincipal.PlatformAdminRole);

        Assert.True(admin.CanActFor("tenant-a"));
        Assert.True(admin.CanActFor("tenant-b"));
    }

    [Fact]
    public void IsPlatformAdmin_ReflectsTheRole()
    {
        Assert.True(Caller("tenant-a", CallerPrincipal.PlatformAdminRole).IsPlatformAdmin);
        Assert.False(Caller("tenant-a").IsPlatformAdmin);
    }
}
