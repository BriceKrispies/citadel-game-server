using System.Net.WebSockets;
using GameServer.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GameServer.IntegrationTests;

/// <summary>
/// Regression guard for the kill-hang. An idle realtime WebSocket parks the server in
/// HandleConnectionAsync awaiting input; the connection loop is linked to
/// <see cref="IHostApplicationLifetime.ApplicationStopping"/>, so signalling a graceful
/// stop must release the in-flight connection promptly — far inside the host
/// ShutdownTimeout — instead of blocking until the timeout force-aborts it (the original
/// "something is holding it open" symptom).
/// </summary>
/// <remarks>
/// Boots the real Host via <see cref="WebApplicationFactory{TEntryPoint}"/> so it exercises
/// the actual Program.cs wiring (endpoint token-linking + HostOptions.ShutdownTimeout), not
/// a hand-rolled copy. Uses real wall-clock time deliberately — this is an integration
/// scenario, kept out of the deterministic fast loop.
/// </remarks>
public sealed class GracefulShutdownScenario
{
    [Fact]
    public async Task OpenRealtimeConnection_IsReleased_PromptlyWhenApplicationStops()
    {
        await using var factory = new WebApplicationFactory<Program>();

        // Touching Services starts the host; capture its lifetime and a token issuer.
        var issuer = factory.Services.GetRequiredService<IJoinTokenIssuer>();
        var lifetime = factory.Services.GetRequiredService<IHostApplicationLifetime>();

        // The realtime endpoint only verifies the token at accept time (identity is checked
        // later, in the Hello we never send), so a signed token is enough to open and hold
        // a connection.
        var token = issuer.Issue("tenant-a", "grid-walk", "room-1", "player-1");
        var uri = new UriBuilder(factory.Server.BaseAddress)
        {
            Scheme = "ws",
            Path = "/realtime/v1/connect",
            Query = $"joinToken={Uri.EscapeDataString(token)}",
        }.Uri;

        using var socket = await factory.Server.CreateWebSocketClient().ConnectAsync(uri, CancellationToken.None);
        Assert.Equal(WebSocketState.Open, socket.State);

        // A read that completes only when the server side ends the connection. The server
        // either closes cleanly or disposes its socket (abnormal close); both mean released.
        var serverReleased = Task.Run(async () =>
        {
            var buffer = new byte[256];
            try
            {
                while ((await socket.ReceiveAsync(buffer, CancellationToken.None)).MessageType != WebSocketMessageType.Close)
                {
                }
            }
            catch (WebSocketException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        });

        // Signal a graceful stop. With the shutdown-linked token this cancels the connection
        // loop immediately; without it the loop keeps blocking and this read never completes.
        lifetime.StopApplication();

        var winner = await Task.WhenAny(serverReleased, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.True(
            winner == serverReleased,
            "the realtime connection was not released within 3s of ApplicationStopping — an in-flight connection is holding the host open.");
    }
}
