using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using GameServer.Protocol.Realtime;
using GameServer.Protocol.Realtime.V1;

namespace GameServer.SecurityTests;

/// <summary>
/// Wire-level fuzzing of the realtime edge: random/truncated protobuf, a text frame on the
/// binary-only channel, an unsupported protocol version, and an oversize frame (the end-to-end
/// coverage for the <c>Realtime:MaxFrameBytes</c> bound — confirmed finding #4). In every case the
/// server must shed the bad input cleanly (typed error and/or close) and STAY UP: each test
/// re-probes <c>/ready == 200</c> afterward.
/// </summary>
public sealed class ProtocolFuzzTests
{
    private static readonly SecurityTarget Target = SecurityTarget.Current;

    private static CancellationToken Ct => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    private async Task<RealtimeWireClient> ConnectAsync()
    {
        var (_, _, token) = await ControlPlane.ProvisionJoinableRoomAsync(Target, Target.TenantAKey!, SecurityTarget.TenantA, Ct);
        return await RealtimeWireClient.ConnectWithTokenAsync(Target, token, Ct);
    }

    private static async Task AssertStillReadyAsync()
    {
        using var client = Target.NewHttpClient();
        var resp = await client.GetAsync("/ready", Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [SkippableFact]
    public async Task RandomGarbageBinary_DoesNotCrashServer()
    {
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);

        await using (var client = await ConnectAsync())
        {
            var garbage = new byte[256];
            RandomNumberGenerator.Fill(garbage);
            await client.SendRawBinaryAsync(garbage, Ct);

            // A malformed (but decodable-as-garbage) frame is answered with a ServerError and the
            // connection is kept alive; an undecodable one is skipped. Either way: no crash.
            await client.SendAsync(WireEnvelopes.Hello(SecurityTarget.TenantA, ControlPlane.GameId), Ct);
            await DrainBrieflyAsync(client);
        }

        await AssertStillReadyAsync();
    }

    [SkippableFact]
    public async Task TruncatedProtobuf_DoesNotCrashServer()
    {
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);

        await using (var client = await ConnectAsync())
        {
            // A valid envelope chopped in half — a classic truncation fuzz.
            var valid = new RealtimeProtobufCodec().Encode(WireEnvelopes.Hello(SecurityTarget.TenantA, ControlPlane.GameId));
            await client.SendRawBinaryAsync(valid[..(valid.Length / 2)], Ct);
            await DrainBrieflyAsync(client);
        }

        await AssertStillReadyAsync();
    }

    [SkippableFact]
    public async Task TextFrameOnBinaryChannel_IsRejectedAndClosed()
    {
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);

        await using (var client = await ConnectAsync())
        {
            await client.SendTextAsync("this is not protobuf", Ct);
            // The edge answers TextFrameNotAllowed and closes the connection.
            var code = await client.ReadUntilServerErrorOrCloseAsync(Ct);
            // Either we observed the typed error, or the close raced ahead — both are acceptable
            // graceful sheds. If we did see an error it must be the text-frame rejection.
            if (code is not null)
            {
                Assert.Equal(ErrorCode.TextFrameNotAllowed, code);
            }
        }

        await AssertStillReadyAsync();
    }

    [SkippableFact]
    public async Task UnsupportedProtocolVersion_IsRejected()
    {
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);

        await using (var client = await ConnectAsync())
        {
            await client.SendAsync(WireEnvelopes.HelloWithUnsupportedVersion(SecurityTarget.TenantA, ControlPlane.GameId), Ct);
            var code = await client.ReadUntilServerErrorOrCloseAsync(Ct);
            if (code is not null)
            {
                Assert.Equal(ErrorCode.UnsupportedProtocolVersion, code);
            }
        }

        await AssertStillReadyAsync();
    }

    [SkippableFact]
    public async Task OversizeFrame_IsDroppedWithoutCrash()
    {
        Skip.IfNot(Target.Configured, SecurityTarget.SkipReason);

        await using (var client = await ConnectAsync())
        {
            // 1 MiB — far above the 64 KiB default MaxFrameBytes. The server caps reassembly and
            // drops the connection instead of buffering toward OOM (finding #4).
            var oversize = new byte[1024 * 1024];
            RandomNumberGenerator.Fill(oversize);
            try
            {
                await client.SendRawBinaryAsync(oversize, Ct);
                // The server-side read aborts reassembly and closes; our next read sees the close.
                await client.ReadUntilServerErrorOrCloseAsync(Ct);
            }
            catch (WebSocketException)
            {
                // Send raced the server-side close — acceptable; the point is it did not crash.
            }
        }

        // The decisive assertion: the process survived the oversize frame.
        await AssertStillReadyAsync();
    }

    /// <summary>Reads whatever the server sends for a short, bounded window, then returns.</summary>
    private static async Task DrainBrieflyAsync(RealtimeWireClient client)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            while (true)
            {
                var env = await client.ReceiveEnvelopeAsync(cts.Token);
                if (env is null)
                {
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or RealtimeProtocolException)
        {
            // Window elapsed, or a frame we cannot decode — either way we are done draining.
        }
    }
}
