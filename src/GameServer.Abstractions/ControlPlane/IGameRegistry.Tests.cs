using GameServer.Replication;
using Xunit;

namespace GameServer.ControlPlane;

public sealed class GameRegistryDtoTests
{
    [Fact]
    public void GameDetail_DefaultsReplicationToNull_MeaningPlatformDefault()
    {
        var detail = new GameDetail("g", "Game", "desc", 1);

        Assert.Null(detail.Replication);
    }

    [Fact]
    public void GameDetail_CarriesAnExplicitReplicationPolicy()
    {
        var detail = new GameDetail("g", "Game", "desc", 1, ReplicationPolicy.Default with { SnapshotMode = SnapshotMode.Delta });

        Assert.Equal(SnapshotMode.Delta, detail.Replication!.SnapshotMode);
    }

    [Fact]
    public void GameVersionDto_CarriesGameVersionAndNotes()
    {
        var v = new GameVersionDto("g", 3, "notes");

        Assert.Equal("g", v.GameId);
        Assert.Equal(3, v.SchemaVersion);
        Assert.Equal("notes", v.Notes);
    }
}
