using System.Net;
using System.Net.WebSockets;
using GameServer.Protocol.Realtime.V1;

namespace GameServer.SecurityTests;

/// <summary>
/// Overload / backpressure behavior, opt-in via <c>[Trait("Category","Dos")]</c>. These probe that
/// the server SHEDS gracefully (typed error / clean close / reap) and STAYS UP under abusive input
/// rather than crashing. Counts and durations are deliberately BOUNDED so the suite can never hang
/// or exhaust the host — the production ceilings (100k connections, 1024 queue depth) are far above
/// what a single test box should attempt, so we assert graceful handling of a bounded burst plus a
/// live <c>/ready</c>, not the absolute ceiling.
/// </summary>
[Trait("Category", "Dos")]
public sealed class DosBackpressureTests
{
    private static readonly SecurityTarget Target = SecurityTarget.Current;

    private static CancellationToken Ct => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private static async Task AssertStillReadyAsync()
    {
        using var client = Target.NewHttpClient();
        var resp = await client.GetAsync("/ready", Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [SkippableFact]
    public async Task ConnectionFlood_IsHandledWithoutCrash()
    {
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);

        // A bounded burst of concurrent realtime connections. Each is admitted or shed; none may
        // take the process down. We hold them briefly, then release.
        const int burst = 40;
        var clients = new List<RealtimeWireClient>();
        try
        {
            var connects = Enumerable.Range(0, burst).Select(async _ =>
            {
                try
                {
                    var (_, _, token) = await ControlPlane.ProvisionJoinableRoomAsync(Target, Target.TenantAKey!, SecurityTarget.TenantA, Ct);
                    return await RealtimeWireClient.ConnectWithTokenAsync(Target, token, Ct);
                }
                catch (WebSocketException)
                {
                    // Shedding a connection (refused upgrade) under load is acceptable, not a crash.
                    return null;
                }
            });

            foreach (var client in await Task.WhenAll(connects))
            {
                if (client is not null)
                {
                    clients.Add(client);
                }
            }

            // At least some connections were admitted; the server is well under its 100k ceiling.
            Assert.NotEmpty(clients);
        }
        finally
        {
            foreach (var client in clients)
            {
                await client.DisposeAsync();
            }
        }

        await AssertStillReadyAsync();
    }

    [SkippableFact]
    public async Task RoomFlood_IsHandledWithoutCrash()
    {
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);

        // A bounded burst of room creations for one tenant. Each is placed or shed (503); the
        // control plane must never fault the process.
        const int burst = 50;
        var creates = Enumerable.Range(0, burst).Select(async _ =>
        {
            try
            {
                await ControlPlane.CreateRoomAsync(Target, Target.TenantAKey!, SecurityTarget.TenantA, Ct);
                return true;
            }
            catch (HttpRequestException)
            {
                // A 503 (cluster-at-capacity) is graceful shedding, not a crash.
                return false;
            }
        });

        await Task.WhenAll(creates);
        await AssertStillReadyAsync();
    }

    [SkippableFact]
    public async Task CommandFlood_ExceedingQueueDepth_ShedsGracefully()
    {
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);

        var (roomId, playerId, token) = await ControlPlane.ProvisionJoinableRoomAsync(Target, Target.TenantAKey!, SecurityTarget.TenantA, Ct);
        await using var client = await RealtimeWireClient.ConnectWithTokenAsync(Target, token, Ct);

        await client.SendAsync(WireEnvelopes.Hello(SecurityTarget.TenantA, ControlPlane.GameId), Ct);
        await AwaitWelcomeAsync(client);
        await client.SendAsync(WireEnvelopes.JoinRoom(SecurityTarget.TenantA, ControlPlane.GameId, roomId, playerId), Ct);

        // Blast well past the 1024 room command-queue depth, fast, with monotonically increasing
        // sequences. The room sheds the overflow (ServerError Overloaded) rather than growing
        // without bound; the server must stay alive.
        const int flood = 4000;
        for (ulong seq = 1; seq <= flood; seq++)
        {
            try
            {
                await client.SendAsync(WireEnvelopes.Command(SecurityTarget.TenantA, ControlPlane.GameId, roomId, playerId, "noop", seq), Ct);
            }
            catch (WebSocketException)
            {
                break; // server closed under us — still not a crash
            }
        }

        // The process is still serving.
        await AssertStillReadyAsync();
    }

    [SkippableFact]
    public async Task Slowloris_HandshakeTimeoutReapsSilentConnection()
    {
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);

        // Open the socket but NEVER complete the handshake (no ClientHello). The server's 1s
        // handshake timeout must reap the connection; we observe the close within a bounded window.
        var (_, _, token) = await ControlPlane.ProvisionJoinableRoomAsync(Target, Target.TenantAKey!, SecurityTarget.TenantA, Ct);
        await using var client = await RealtimeWireClient.ConnectWithTokenAsync(Target, token, Ct);

        using var window = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // The server reaps the silent connection; our read returns null (closed) within the window.
        var raw = await client.ReadUntilServerErrorOrCloseAsync(window.Token);
        // A reaped handshake closes without a ServerError frame.
        Assert.Null(raw);

        await AssertStillReadyAsync();
    }

    private static async Task AwaitWelcomeAsync(RealtimeWireClient client)
    {
        var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;
        while (true)
        {
            var env = await client.ReceiveEnvelopeAsync(ct);
            if (env is null || env.PayloadCase == RealtimeEnvelope.PayloadOneofCase.ServerWelcome)
            {
                return;
            }
        }
    }
}
