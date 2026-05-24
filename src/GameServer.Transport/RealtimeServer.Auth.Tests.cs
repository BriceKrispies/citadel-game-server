using GameServer.Identity;
using GameServer.Observability;
using GameServer.Protocol;
using GameServer.Transport.Testing;
using Xunit;

namespace GameServer.Transport;

/// <summary>
/// Proves the realtime handshake treats the verified join-token claims as
/// authoritative identity: a client cannot connect as a tenant/game it was not
/// authorized for, nor join as a different player or into a different room than its
/// token was minted for. These close the gap where identity was self-declared in
/// ClientHello/ClientJoinRoom and never bound to the token.
/// </summary>
public sealed class RealtimeServerAuthTests
{
    private static readonly RoomId Arena = new("arena");

    private static JoinTokenClaims Principal(string tenant, string game, string room, string player) =>
        new(tenant, game, room, player, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1));

    [Fact]
    public async Task Hello_DeclaringDifferentTenantThanToken_IsRejected_WithoutSession()
    {
        var harness = new SliceHarness("tenant-a", "tenant-b");
        var (transport, client) = harness.NewClient("c1", "tenant-a", "p1");

        client.Hello();
        client.Close();

        // The token authorizes tenant-b, but the client declared tenant-a in its hello.
        var tokenForOtherTenant = Principal("tenant-b", SliceHarness.DefaultGame, "arena", "p1");
        await harness.Server.HandleConnectionAsync(transport, tokenForOtherTenant);

        var error = Assert.IsType<ServerError>(Assert.Single(client.Received()).Payload);
        Assert.Equal(ServerErrorCode.Unauthorized, error.Code);
        Assert.DoesNotContain(client.Received(), m => m.Payload is ServerWelcome);
        Assert.True(harness.Telemetry.HasEvent(TelemetryEvents.IdentityRejected));
    }

    [Fact]
    public async Task Join_AsDifferentPlayerThanToken_IsRejected()
    {
        var harness = new SliceHarness("tenant-a");
        var (transport, client) = harness.NewClient("c1", "tenant-a", "p1");

        client.Hello();
        client.Join(Arena);
        client.Close();

        // Hello identity matches, but the token authorizes player p2 — not p1.
        var tokenForOtherPlayer = Principal("tenant-a", SliceHarness.DefaultGame, "arena", "p2");
        await harness.Server.HandleConnectionAsync(transport, tokenForOtherPlayer);

        var error = client.Received().Select(m => m.Payload).OfType<ServerError>().Single();
        Assert.Equal(ServerErrorCode.Unauthorized, error.Code);
        // Rejected before placement: no room was created for this unauthorized join.
        Assert.False(harness.Router.TryGetRoom(harness.Key("tenant-a", "arena"), out _));
    }

    [Fact]
    public async Task Join_IntoDifferentRoomThanToken_IsRejected()
    {
        var harness = new SliceHarness("tenant-a");
        var (transport, client) = harness.NewClient("c1", "tenant-a", "p1");

        client.Hello();
        client.Join(Arena); // declares "arena"
        client.Close();

        // Token authorizes a different room.
        var tokenForOtherRoom = Principal("tenant-a", SliceHarness.DefaultGame, "vault", "p1");
        await harness.Server.HandleConnectionAsync(transport, tokenForOtherRoom);

        var error = client.Received().Select(m => m.Payload).OfType<ServerError>().Single();
        Assert.Equal(ServerErrorCode.Unauthorized, error.Code);
    }

    [Fact]
    public async Task Handshake_MatchingToken_IsAccepted()
    {
        var harness = new SliceHarness("tenant-a");
        var (transport, client) = harness.NewClient("c1", "tenant-a", "p1");

        client.Hello();
        client.Join(Arena);
        client.Close();

        // client.Principal exactly matches the declared tenant/game/room/player.
        await harness.Server.HandleConnectionAsync(transport, client.Principal);

        Assert.Contains(client.Received(), m => m.Payload is ServerWelcome);
        Assert.DoesNotContain(client.Received(), m => m.Payload is ServerError);
        Assert.True(harness.Router.TryGetRoom(harness.Key("tenant-a", "arena"), out var room));
        Assert.True(room.HasPlayer(new PlayerId("p1")));
    }
}
