using System.Net;
using System.Net.Http.Json;
using GameServer.Protocol.Realtime.V1;

namespace GameServer.SecurityTests;

/// <summary>
/// Cross-tenant isolation over the wire: a tenant-A caller must not touch tenant-B resources
/// (403 on the control plane), and a connection holding a tenant-A join token must not act as
/// tenant-B over the realtime socket (<c>ServerError(Unauthorized)</c>).
/// </summary>
public sealed class TenantIsolationTests
{
    private static readonly SecurityTarget Target = SecurityTarget.Current;

    private static CancellationToken Ct => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    [SkippableFact]
    public async Task TenantA_CannotReadTenantBRoom_Is403()
    {
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);

        // tenant-B creates a room; tenant-A then tries to read it.
        var room = await ControlPlane.CreateRoomAsync(Target, Target.TenantBKey!, SecurityTarget.TenantB, Ct);

        using var attacker = Target.NewHttpClient(Target.TenantAKey);
        var resp = await attacker.GetAsync($"/api/v1/rooms/{room.RoomId}", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [SkippableFact]
    public async Task TenantA_CannotMintTokenForTenantBRoom_Is403()
    {
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);

        var room = await ControlPlane.CreateRoomAsync(Target, Target.TenantBKey!, SecurityTarget.TenantB, Ct);

        using var attacker = Target.NewHttpClient(Target.TenantAKey);
        var resp = await attacker.PostAsJsonAsync(
            $"/api/v1/rooms/{room.RoomId}/join-token", new { PlayerId = "intruder" }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [SkippableFact]
    public async Task TenantA_CannotCreateSessionForTenantB_Is403()
    {
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);

        using var attacker = Target.NewHttpClient(Target.TenantAKey);
        var resp = await attacker.PostAsJsonAsync(
            "/api/v1/sessions", new { TenantId = SecurityTarget.TenantB, PlayerId = "intruder" }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [SkippableFact]
    public async Task TenantA_CannotAdminObserveTenantBRoom_Is403()
    {
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);

        using var attacker = Target.NewHttpClient(Target.TenantAKey);
        var resp = await attacker.GetAsync($"/api/v1/admin/rooms/{SecurityTarget.TenantB}/any-room", Ct);
        // A cross-tenant observe is denied (403) before any room lookup leaks existence.
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [SkippableFact]
    public async Task CrossTenantJoinOverWebSocket_IsUnauthorized()
    {
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);

        // A genuine tenant-A token; the client then tries to HELLO as tenant-B over the socket.
        var (_, _, token) = await ControlPlane.ProvisionJoinableRoomAsync(Target, Target.TenantAKey!, SecurityTarget.TenantA, Ct);

        await using var client = await RealtimeWireClient.ConnectWithTokenAsync(Target, token, Ct);
        await client.SendAsync(WireEnvelopes.Hello(SecurityTarget.TenantB, ControlPlane.GameId), Ct);

        // The server must reject and grant no access, AND surface the honest wire code UNAUTHORIZED
        // (Finding 5 fixed the mapper that previously fell through to INTERNAL_SERVER_ERROR).
        var outcome = await client.ReadUntilRejectedSnapshotOrCloseAsync(Ct);
        Assert.False(outcome.AccessWasGranted, "Cross-tenant connection was granted access.");
        Assert.True(outcome.WasRejected, "Cross-tenant hello was not rejected.");
        Assert.Equal(ErrorCode.Unauthorized, outcome.ErrorCode);
    }
}
