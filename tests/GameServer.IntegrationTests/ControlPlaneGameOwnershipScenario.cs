using System.Net;
using System.Net.Http.Json;
using GameServer.ControlPlane;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// Adversarial (Wave 7 red-team): a game is tenant-scoped data, so a game-admin scoped to tenant-B must
/// NOT be able to mutate a game OWNED by tenant-A — neither delete it nor register a new version of it.
/// The mutation gate must authorize against the game's OWNING tenant, not merely the caller's own tenant.
/// Pins the cross-tenant game-mutation hole: <c>DELETE /games/{id}</c> and <c>POST /games/{id}/versions</c>
/// gated on <c>caller.TenantId</c> instead of the game's owner.
/// </summary>
public sealed class ControlPlaneGameOwnershipScenario
{
    private readonly ITestOutputHelper _output;

    public ControlPlaneGameOwnershipScenario(ITestOutputHelper output) => _output = output;

    private const string GameAdminA = "dev-gameadmin-a-key"; // game-admin, tenant-a
    private const string GameAdminB = "dev-gameadmin-b-key"; // game-admin, tenant-b
    private const string PlatformAdmin = "dev-admin-key";    // platform-admin, tenant-a

    private static HttpClient Client(WebApplicationFactory<Program> host, string key)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", key);
        return client;
    }

    [Fact]
    public async Task GameAdmin_CannotDelete_AnotherTenantsGame()
    {
        using var host = new WebApplicationFactory<Program>();
        var audit = host.Services.GetRequiredService<IAuditLog>();
        var catalog = host.Services.GetRequiredService<IGameRegistry>();

        // tenant-a's game-admin creates a game owned by tenant-a.
        var created = await Client(host, GameAdminA).PostAsJsonAsync("/api/v1/games",
            new { tenantId = "tenant-a", gameId = "owned-by-a", name = "Owned By A", description = "", protocolVersion = 1 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // tenant-b's game-admin attempts to DELETE tenant-a's game. This is a cross-tenant destructive
        // write and must be forbidden — and must NOT actually delete the game.
        var crossDelete = await Client(host, GameAdminB).DeleteAsync("/api/v1/games/owned-by-a");
        Assert.Equal(HttpStatusCode.Forbidden, crossDelete.StatusCode);

        // The game still exists (the denial did not mutate tenant-a's resource).
        Assert.True(catalog.TryGet("owned-by-a", out _));

        // The denial is audited.
        Assert.Contains(audit.Read(), r => r.Action == "delete-game" && r.Outcome == "denied");

        // The owning tenant's game-admin CAN delete it.
        var ownDelete = await Client(host, GameAdminA).DeleteAsync("/api/v1/games/owned-by-a");
        Assert.Equal(HttpStatusCode.NoContent, ownDelete.StatusCode);
        Assert.False(catalog.TryGet("owned-by-a", out _));

        _output.WriteLine("game-admin-B denied delete of tenant-a's game; tenant-a admin allowed");
    }

    [Fact]
    public async Task GameAdmin_CannotRegisterVersion_OnAnotherTenantsGame()
    {
        using var host = new WebApplicationFactory<Program>();
        var audit = host.Services.GetRequiredService<IAuditLog>();
        var catalog = host.Services.GetRequiredService<IGameRegistry>();

        // tenant-a's game-admin creates a game owned by tenant-a.
        var created = await Client(host, GameAdminA).PostAsJsonAsync("/api/v1/games",
            new { tenantId = "tenant-a", gameId = "ver-owned-by-a", name = "A", description = "", protocolVersion = 1 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // tenant-b's game-admin attempts to register a NEW VERSION on tenant-a's game — cross-tenant write.
        var crossVersion = await Client(host, GameAdminB).PostAsJsonAsync("/api/v1/games/ver-owned-by-a/versions",
            new { schemaVersion = 99, notes = "injected-by-b" });
        Assert.Equal(HttpStatusCode.Forbidden, crossVersion.StatusCode);

        // The version was NOT created.
        Assert.DoesNotContain(catalog.ListVersions("ver-owned-by-a"), v => v.SchemaVersion == 99);

        // Denial audited.
        Assert.Contains(audit.Read(), r => r.Action == "create-game-version" && r.Outcome == "denied");

        // The owning tenant's game-admin CAN register a version.
        var ownVersion = await Client(host, GameAdminA).PostAsJsonAsync("/api/v1/games/ver-owned-by-a/versions",
            new { schemaVersion = 2, notes = "legit" });
        Assert.Equal(HttpStatusCode.Created, ownVersion.StatusCode);
        Assert.Contains(catalog.ListVersions("ver-owned-by-a"), v => v.SchemaVersion == 2);

        _output.WriteLine("game-admin-B denied version on tenant-a's game; tenant-a admin allowed");
    }

    [Fact]
    public async Task PlatformAdmin_CanMutate_AnyTenantsGame()
    {
        using var host = new WebApplicationFactory<Program>();
        var catalog = host.Services.GetRequiredService<IGameRegistry>();

        // game owned by tenant-b.
        var created = await Client(host, GameAdminB).PostAsJsonAsync("/api/v1/games",
            new { tenantId = "tenant-b", gameId = "owned-by-b", name = "B", description = "", protocolVersion = 1 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // platform-admin (whose own tenant is tenant-a) may still version + delete tenant-b's game.
        var version = await Client(host, PlatformAdmin).PostAsJsonAsync("/api/v1/games/owned-by-b/versions",
            new { schemaVersion = 5, notes = "platform" });
        Assert.Equal(HttpStatusCode.Created, version.StatusCode);

        var deleted = await Client(host, PlatformAdmin).DeleteAsync("/api/v1/games/owned-by-b");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.False(catalog.TryGet("owned-by-b", out _));

        _output.WriteLine("platform-admin retains full authority across tenants");
    }
}
