using GameServer.Protocol;
using GameServer.Replication;
using Xunit;

namespace GameServer.Simulation;

public sealed class GridWalkGameTests
{
    private static readonly PlayerId Player = new("p1");

    private static (int X, int Y) Pos(IGameSimulation game) =>
        GridWalkGame.Decode(game.Project().Single(e => e.Id == new EntityId("p1")).Payload);

    [Fact]
    public void Join_PlacesWalkerAtOrigin()
    {
        var game = new GridWalkGame();
        game.Join(Player);

        Assert.True(game.HasPlayer(Player));
        Assert.Equal((0, 0), Pos(game));
    }

    [Theory]
    [InlineData("Up", 0, 1)]
    [InlineData("Down", 0, -1)]
    [InlineData("Left", -1, 0)]
    [InlineData("Right", 1, 0)]
    public void Apply_MovesInTheCommandedDirection(string command, int x, int y)
    {
        var game = new GridWalkGame();
        game.Join(Player);

        game.Apply(Player, command);

        Assert.Equal((x, y), Pos(game));
    }

    [Fact]
    public void CanAccept_OnlyTheFourDirections()
    {
        var game = new GridWalkGame();
        game.Join(Player);

        Assert.True(game.CanAccept(Player, "Up"));
        Assert.True(game.CanAccept(Player, "Right"));
        Assert.False(game.CanAccept(Player, "MoveRight")); // a different game's command
        Assert.False(game.CanAccept(Player, "Jump"));
    }

    [Fact]
    public void Version_ChangesOnEachMove_EvenWhenReturningToAPriorCell()
    {
        var game = new GridWalkGame();
        game.Join(Player);
        var v0 = game.Project().Single().Version;

        game.Apply(Player, "Up");
        var v1 = game.Project().Single().Version;
        game.Apply(Player, "Down"); // back to origin, but state still "changed"
        var v2 = game.Project().Single().Version;

        Assert.Equal((0, 0), Pos(game));
        Assert.True(v1 != v0 && v2 != v1); // versions strictly progress so deltas fire
    }

    [Fact]
    public void Project_CarriesPositionInRelevanceKey()
    {
        var game = new GridWalkGame();
        game.Join(Player);
        game.Apply(Player, "Up");
        game.Apply(Player, "Right");

        var key = game.Project().Single().Key;
        Assert.Equal(1, key.X);
        Assert.Equal(1, key.Y);
    }

    [Fact]
    public void Version_IncrementsByExactlyOne_OnEachMove()
    {
        // Stronger than "changes": the bump is +1 (a monotonically increasing counter), not merely
        // "different". A -1 step would still differ each move yet break the delta layer's assumption
        // that the version only ever advances.
        var game = new GridWalkGame();
        game.Join(Player);
        var v0 = game.Project().Single().Version;

        game.Apply(Player, "Up");
        var v1 = game.Project().Single().Version;
        game.Apply(Player, "Down");
        var v2 = game.Project().Single().Version;

        Assert.Equal(v0 + 1, v1);
        Assert.Equal(v1 + 1, v2);
    }

    [Fact]
    public void Project_RelevanceKeyGroupIsEmpty()
    {
        var game = new GridWalkGame();
        game.Join(Player);

        Assert.Equal(string.Empty, game.Project().Single().Key.Group);
    }

    [Fact]
    public void SerializeRestore_RoundTripsState()
    {
        var game = new GridWalkGame();
        game.Join(Player);
        game.Apply(Player, "Up");
        game.Apply(Player, "Right");
        game.Apply(Player, "Right");

        var restored = new GridWalkGame();
        restored.Restore(game.Serialize());

        Assert.True(restored.HasPlayer(Player));
        Assert.Equal((2, 1), Pos(restored));
    }

    [Fact]
    public void Restore_ReplacesPriorState_RatherThanMergingIntoIt()
    {
        // Restore must clear existing state first: a room reused for a different restore must not
        // retain a stale walker that the restored snapshot does not contain.
        var game = new GridWalkGame();
        game.Join(new PlayerId("stale"));

        var source = new GridWalkGame();
        source.Join(Player);
        game.Restore(source.Serialize());

        Assert.True(game.HasPlayer(Player));
        Assert.False(game.HasPlayer(new PlayerId("stale"))); // the pre-restore walker is gone
    }

    [Fact]
    public void Restore_FromJsonNull_YieldsEmptyState_WithoutThrowing()
    {
        // A serialized `null` (not an object) must restore to an empty world, not crash — the
        // null-coalescing fallback in Restore is the guard that makes this safe.
        var game = new GridWalkGame();

        game.Restore(System.Text.Encoding.UTF8.GetBytes("null"));

        Assert.Empty(game.Project());
    }
}
