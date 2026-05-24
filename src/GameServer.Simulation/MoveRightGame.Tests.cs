using GameServer.Protocol;
using GameServer.Replication;
using Xunit;

namespace GameServer.Simulation;

public sealed class MoveRightGameTests
{
    private static readonly PlayerId Player = new("p1");

    private static int X(IGameSimulation game) =>
        MoveRightGame.DecodeX(game.Project().Single(e => e.Id == new EntityId("p1")).Payload);

    [Fact]
    public void Join_PlacesPlayerAtOrigin()
    {
        var game = new MoveRightGame();
        game.Join(Player);

        Assert.True(game.HasPlayer(Player));
        Assert.Equal(0, X(game));
    }

    [Fact]
    public void CanAccept_OnlyMoveRight()
    {
        var game = new MoveRightGame();
        game.Join(Player);

        Assert.True(game.CanAccept(Player, "MoveRight"));
        Assert.False(game.CanAccept(Player, "MoveLeft"));
    }

    [Fact]
    public void Apply_MoveRight_IncrementsX_AndChangesVersion()
    {
        var game = new MoveRightGame();
        game.Join(Player);
        var before = game.Project().Single();

        game.Apply(Player, "MoveRight");
        var after = game.Project().Single();

        Assert.Equal(1, X(game));
        Assert.NotEqual(before.Version, after.Version); // version must change so the delta fires
    }

    [Fact]
    public void Apply_UnknownCommand_Throws()
    {
        var game = new MoveRightGame();
        game.Join(Player);

        Assert.Throws<ArgumentException>(() => game.Apply(Player, "Nope"));
    }

    [Fact]
    public void Project_CarriesPositionInRelevanceKey()
    {
        var game = new MoveRightGame();
        game.Join(Player);
        game.Apply(Player, "MoveRight");

        Assert.Equal(1, game.Project().Single().Key.X);
    }

    [Fact]
    public void SerializeRestore_RoundTripsState()
    {
        var game = new MoveRightGame();
        game.Join(Player);
        game.Apply(Player, "MoveRight");
        game.Apply(Player, "MoveRight");

        var restored = new MoveRightGame();
        restored.Restore(game.Serialize());

        Assert.True(restored.HasPlayer(Player));
        Assert.Equal(2, X(restored));
    }
}
