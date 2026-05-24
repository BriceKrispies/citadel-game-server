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
}
