using GameServer.Protocol;
using GameServer.Replication;
using GameServer.Simulation;
using GameServer.Transport;
using Xunit;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// Wave 2 — room-lifecycle hooks observed end to end through the real realtime stack. A sample
/// game (<see cref="MoveRightGame"/>) implements the platform's lifecycle contract — <c>CanJoin</c>
/// (reject path), <c>OnLeave</c>, <c>OnTerminate</c> — and these scenarios prove the data plane
/// actually calls them at the right call sites:
///   • #10 LeaveRoomFreesMembership: an explicit <c>ClientLeaveRoom</c> frees the player's room
///     membership AND fires <c>OnLeave</c> (observable on the game), without dropping the connection.
///   • CanJoin reject: a game that refuses a join turns it away with a typed error and NO membership.
///   • OnTerminate: the room's last leaver triggers the game's terminate hook (Reap lifecycle).
/// The hooks are observed on the game instance the room actually hosts (captured via the factory),
/// so the test cannot be satisfied by anything other than the platform invoking them.
/// </summary>
public sealed class RoomLifecycleHooksScenario
{
    private readonly ITestOutputHelper _output;

    public RoomLifecycleHooksScenario(ITestOutputHelper output) => _output = output;

    /// <summary>An integration harness that hands back the single game instance it created per room.</summary>
    private static (IntegrationHarness Harness, Func<MoveRightGame> Game) HarnessCapturingGame(
        RoomLifecycle lifecycle = RoomLifecycle.Reap, int maxPlayers = 0)
    {
        MoveRightGame? captured = null;
        var harness = new IntegrationHarness(
            _ => captured = new MoveRightGame { MaxPlayers = maxPlayers },
            lifecycle: lifecycle);
        return (harness, () => captured ?? throw new InvalidOperationException("No game was created yet."));
    }

    [Fact]
    public async Task LeaveRoom_FreesMembership_AndFiresOnLeave()
    {
        var (harness, game) = HarnessCapturingGame(RoomLifecycle.Reap);
        var key = harness.Key("tenant-a", "arena");

        var transport = await harness.RunClientWithLeaveAsync("tenant-a", "arena", "p1");

        // OnLeave fired and is observable on the sample game.
        Assert.Contains(new PlayerId("p1"), game().LeftPlayers);

        // A ServerEvent(player_left) was pushed to the (still-connected) client.
        var pushed = transport.DrainOutbound();
        Assert.Contains(pushed, m =>
            m.Payload is ServerEvent ev && ev.EventType == ServerEvent.Types.PlayerLeft);

        // Membership is freed: under Reap the now-empty room is torn down (no longer active/observable).
        Assert.DoesNotContain(key, harness.Server.ActiveRooms);
        Assert.False(harness.Server.TryObserveRoom(key, out _));

        _output.WriteLine($"after explicit leave — left players observed: {string.Join(",", game().LeftPlayers.Select(p => p.Value))}");
    }

    [Fact]
    public async Task CanJoin_Reject_TurnsAwayJoin_WithTypedError_AndNoMembership()
    {
        // The game caps the room at one player; a second join must be refused by the game's rule.
        var (harness, game) = HarnessCapturingGame(RoomLifecycle.Persist, maxPlayers: 1);

        // First player is admitted.
        await harness.RunClientAsync("tenant-a", "arena", "p1", "demo", Array.Empty<string>());
        Assert.True(game().HasPlayer(new PlayerId("p1")));

        // Second player: the game's CanJoin refuses.
        var refused = await harness.RunClientAsync("tenant-a", "arena", "p2", "demo", Array.Empty<string>());

        // No membership granted to p2.
        Assert.False(game().HasPlayer(new PlayerId("p2")));

        // The refusal surfaces as a typed ServerError (NotJoined), not a silent drop or a snapshot.
        var pushed = refused.DrainOutbound();
        Assert.Contains(pushed, m =>
            m.Payload is ServerError err && err.Code == ServerErrorCode.NotJoined);
        Assert.DoesNotContain(pushed, m => m.Payload is ServerSnapshot);

        _output.WriteLine("second join refused by the game's CanJoin rule with a typed NotJoined error.");
    }

    /// <summary>A sample game that refuses one named player but admits everyone else.</summary>
    private sealed class BanListGame : IGameSimulation
    {
        private readonly string _banned;
        private readonly HashSet<PlayerId> _players = new();

        public BanListGame(string banned) => _banned = banned;

        public bool CanJoin(PlayerId player) => player.Value != _banned;
        public void Join(PlayerId player) => _players.Add(player);
        public bool HasPlayer(PlayerId player) => _players.Contains(player);
        public bool CanAccept(PlayerId player, string command) => true;
        public void Apply(PlayerId player, string command) { }
        public IReadOnlyList<EntitySnapshot> Project() =>
            _players.Select(p => new EntitySnapshot(new EntityId(p.Value), 1, RelevanceKey.None, Array.Empty<byte>())).ToArray();
        public byte[] Serialize() => Array.Empty<byte>();
        public void Restore(byte[] state) { }
    }

    [Fact]
    public async Task CanJoin_RefusingFirstJoinIntoNewRoom_StrandsNoRoom_AndAllowsLaterLegitJoin()
    {
        // The FIRST join into a brand-new room is refused by the game (isNewRoom=true + CanJoin=false):
        // the room was created only for this refused join, so it must be torn down, leaving NO orphaned
        // room — and crucially NOT blocking a later legitimate join into the same room key.
        // Persist lifecycle so the LATER legit join's room stays observable after its client closes
        // (under Reap a closed last-subscriber is reaped immediately, which would mask the assertion).
        // The refused-first-join teardown under test runs regardless of lifecycle (HandleJoinAsync).
        var harness = new IntegrationHarness(_ => new BanListGame("p-banned"), lifecycle: RoomLifecycle.Persist);
        var key = harness.Key("tenant-a", "arena");

        // Banned player attempts the very first join into the (not-yet-existing) room.
        var refused = await harness.RunClientAsync("tenant-a", "arena", "p-banned", "demo", Array.Empty<string>());
        Assert.Contains(refused.DrainOutbound(), m => m.Payload is ServerError err && err.Code == ServerErrorCode.NotJoined);

        // No room was stranded by the refused first join.
        Assert.DoesNotContain(key, harness.Server.ActiveRooms);
        Assert.False(harness.Server.TryObserveRoom(key, out _));

        // A later legitimate join into the SAME room key must succeed and create the room normally —
        // proving the refused join left no half-created/blocking room behind.
        await harness.JoinAsync("tenant-a", "arena", "p1");
        Assert.Contains(key, harness.Server.ActiveRooms);
        Assert.True(harness.Server.TryObserveRoom(key, out var obs));
        Assert.Contains(obs.Entities, e => e.EntityId == "p1");

        _output.WriteLine("refused first join stranded no room; a later legit join created the room cleanly.");
    }

    [Fact]
    public async Task OnTerminate_Fires_WhenRoomIsReaped()
    {
        var (harness, game) = HarnessCapturingGame(RoomLifecycle.Reap);
        var key = harness.Key("tenant-a", "arena");

        // A single client joins and cleanly disconnects: it is the last (only) subscriber, so the
        // room is reaped — and the game's OnTerminate must fire as the room is torn down.
        await harness.RunClientAsync("tenant-a", "arena", "p1", "demo", Array.Empty<string>());

        Assert.True(game().Terminated, "OnTerminate must fire when the room is reaped");
        Assert.DoesNotContain(key, harness.Server.ActiveRooms);

        _output.WriteLine($"room reaped; game terminated = {game().Terminated}");
    }

    [Fact]
    public async Task GameThatIgnoresHooks_BehavesUnchanged_Liskov()
    {
        // GridWalkGame implements none of the new hooks (it relies on the no-op defaults). Joining,
        // moving, and ticking it must work exactly as before — proof the defaults are honestly
        // substitutable and add no behavior.
        var harness = new IntegrationHarness(_ => new GridWalkGame(), lifecycle: RoomLifecycle.Persist);
        var key = harness.Key("tenant-a", "grid");

        await harness.RunClientAsync("tenant-a", "grid", "p1", "grid-walk", new[] { GridWalkGame.Right });
        await harness.Server.TickRoom(key);

        Assert.True(harness.Snapshots.TryGetLatest(key, out _));
        Assert.Contains(key, harness.Server.ActiveRooms);
    }
}
