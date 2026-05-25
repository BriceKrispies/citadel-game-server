using GameServer.Protocol.Realtime;
using GameServer.Protocol.Realtime.V1;

namespace GameServer.SecurityTests;

/// <summary>
/// A connection holds a VALID token (the upgrade succeeds), then lies in its handshake messages —
/// declaring a different tenant, game, room, or player than the token authorizes. The server treats
/// the verified token as authoritative: it REJECTS the mismatch with a <c>ServerError</c> and NEVER
/// grants access (no snapshot for the lied-about scope).
/// </summary>
/// <remarks>
/// The security-decisive assertion is "rejected AND no access granted". The wire error code is now
/// also asserted as <c>UNAUTHORIZED</c>: the kernel rejects with <c>ServerErrorCode.Unauthorized</c>
/// and the wire mapper maps it honestly (Finding 5 fixed the prior fall-through to
/// <c>INTERNAL_SERVER_ERROR</c>).
/// </remarks>
public sealed class RealtimeScopeEscalationTests
{
    private static readonly SecurityTarget Target = SecurityTarget.Current;

    private static CancellationToken Ct => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    private static void AssertDeniedNoAccess(WireOutcome outcome)
    {
        Assert.False(outcome.AccessWasGranted, "Server granted access to a scope the token did not authorize.");
        Assert.True(outcome.WasRejected, "Server neither rejected the mismatch nor closed cleanly.");
        // The honest wire code for an authorization rejection is UNAUTHORIZED (Finding 5). A clean
        // close with no ServerError frame (ErrorCode null) also denies access and is acceptable; only
        // a wrong, non-null code (e.g. the old INTERNAL_SERVER_ERROR fall-through) is a regression.
        if (outcome.ErrorCode is { } code)
        {
            Assert.Equal(ErrorCode.Unauthorized, code);
        }
    }

    [SkippableFact]
    public async Task HelloDeclaringDifferentTenant_IsDenied()
    {
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);

        var (_, _, token) = await ControlPlane.ProvisionJoinableRoomAsync(Target, Target.TenantAKey!, SecurityTarget.TenantA, Ct);
        await using var client = await RealtimeWireClient.ConnectWithTokenAsync(Target, token, Ct);

        // Token is tenant-a; hello claims tenant-b.
        await client.SendAsync(WireEnvelopes.Hello(SecurityTarget.TenantB, ControlPlane.GameId), Ct);

        AssertDeniedNoAccess(await client.ReadUntilRejectedSnapshotOrCloseAsync(Ct));
    }

    [SkippableFact]
    public async Task HelloDeclaringDifferentGame_IsDenied()
    {
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);

        var (_, _, token) = await ControlPlane.ProvisionJoinableRoomAsync(Target, Target.TenantAKey!, SecurityTarget.TenantA, Ct);
        await using var client = await RealtimeWireClient.ConnectWithTokenAsync(Target, token, Ct);

        // Token's game is the room's game; hello claims a different game.
        await client.SendAsync(WireEnvelopes.Hello(SecurityTarget.TenantA, "some-other-game"), Ct);

        AssertDeniedNoAccess(await client.ReadUntilRejectedSnapshotOrCloseAsync(Ct));
    }

    [SkippableFact]
    public async Task JoinDeclaringDifferentRoom_IsDenied()
    {
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);

        var (_, playerId, token) = await ControlPlane.ProvisionJoinableRoomAsync(Target, Target.TenantAKey!, SecurityTarget.TenantA, Ct);
        await using var client = await RealtimeWireClient.ConnectWithTokenAsync(Target, token, Ct);

        // Truthful hello so we reach the join handler.
        await client.SendAsync(WireEnvelopes.Hello(SecurityTarget.TenantA, ControlPlane.GameId), Ct);
        await AwaitWelcomeAsync(client);

        // Join a room the token was NOT minted for, as the authorized player.
        await client.SendAsync(WireEnvelopes.JoinRoom(SecurityTarget.TenantA, ControlPlane.GameId, "room-the-token-does-not-grant", playerId), Ct);

        AssertDeniedNoAccess(await client.ReadUntilRejectedSnapshotOrCloseAsync(Ct));
    }

    [SkippableFact]
    public async Task JoinDeclaringDifferentPlayer_IsDenied()
    {
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);

        var (roomId, _, token) = await ControlPlane.ProvisionJoinableRoomAsync(Target, Target.TenantAKey!, SecurityTarget.TenantA, Ct);
        await using var client = await RealtimeWireClient.ConnectWithTokenAsync(Target, token, Ct);

        await client.SendAsync(WireEnvelopes.Hello(SecurityTarget.TenantA, ControlPlane.GameId), Ct);
        await AwaitWelcomeAsync(client);

        // Correct room, but impersonate a different player than the token authorizes.
        await client.SendAsync(WireEnvelopes.JoinRoom(SecurityTarget.TenantA, ControlPlane.GameId, roomId, "someone-else"), Ct);

        AssertDeniedNoAccess(await client.ReadUntilRejectedSnapshotOrCloseAsync(Ct));
    }

    [SkippableFact]
    public async Task TruthfulHelloThenJoin_GrantsAccess()
    {
        // Positive control: the legitimate scope joins cleanly (a ServerSnapshot eventually flows
        // and NO rejection), proving the denials above are about the lie, not a broken join path.
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);

        var (roomId, playerId, token) = await ControlPlane.ProvisionJoinableRoomAsync(Target, Target.TenantAKey!, SecurityTarget.TenantA, Ct);
        await using var client = await RealtimeWireClient.ConnectWithTokenAsync(Target, token, Ct);

        await client.SendAsync(WireEnvelopes.Hello(SecurityTarget.TenantA, ControlPlane.GameId), Ct);
        await AwaitWelcomeAsync(client);
        await client.SendAsync(WireEnvelopes.JoinRoom(SecurityTarget.TenantA, ControlPlane.GameId, roomId, playerId), Ct);

        var outcome = await client.ReadUntilRejectedSnapshotOrCloseAsync(Ct);
        Assert.True(outcome.AccessWasGranted, "A legitimate join should produce a snapshot.");
        Assert.False(outcome.WasRejected, "A legitimate join should not be rejected.");
    }

    /// <summary>Reads frames until the ServerWelcome arrives (or the connection drops).</summary>
    private static async Task AwaitWelcomeAsync(RealtimeWireClient client)
    {
        var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;
        while (true)
        {
            RealtimeEnvelope? env;
            try
            {
                env = await client.ReceiveEnvelopeAsync(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or RealtimeProtocolException)
            {
                return;
            }

            if (env is null || env.PayloadCase == RealtimeEnvelope.PayloadOneofCase.ServerWelcome)
            {
                return;
            }
        }
    }
}
