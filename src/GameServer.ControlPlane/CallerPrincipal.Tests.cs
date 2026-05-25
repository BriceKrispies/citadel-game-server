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

    [Fact]
    public void CanMutateTenantResources_ReadOnlyOperator_IsDenied()
    {
        // Least privilege: a read-only operator may read, never mutate.
        Assert.False(Caller("tenant-a", CallerPrincipal.OperatorRole).CanMutateTenantResources);
        Assert.False(Caller("tenant-a").CanMutateTenantResources); // no role at all
    }

    [Fact]
    public void CanMutateTenantResources_GameAdminAndPlatformAdmin_AreAllowed()
    {
        Assert.True(Caller("tenant-a", CallerPrincipal.GameAdminRole).CanMutateTenantResources);
        Assert.True(Caller("tenant-a", CallerPrincipal.PlatformAdminRole).CanMutateTenantResources);
    }

    [Fact]
    public void GameAdmin_IsNotPlatformAdmin_NoEscalation()
    {
        // A game-admin can mutate tenant resources but must NOT pass platform-level checks: holding a
        // tenant-mutation role never escalates to fleet/provisioning authority.
        var gameAdmin = Caller("tenant-a", CallerPrincipal.GameAdminRole);

        Assert.True(gameAdmin.CanMutateTenantResources);
        Assert.False(gameAdmin.IsPlatformAdmin);
    }

    [Fact]
    public void GameAdmin_CannotActForAnotherTenant()
    {
        // Tenant authority is independent of the mutation role: a game-admin scoped to tenant-a may not
        // act for tenant-b even though it CAN mutate (its own tenant's) resources.
        var gameAdmin = Caller("tenant-a", CallerPrincipal.GameAdminRole);

        Assert.True(gameAdmin.CanActFor("tenant-a"));
        Assert.False(gameAdmin.CanActFor("tenant-b"));
    }
}
