using Xunit;

namespace GameServer.Replication;

public sealed class ReplicationModelTests
{
    [Fact]
    public void RelevanceKey_None_IsEmptyOrigin()
    {
        Assert.Equal(0, RelevanceKey.None.X);
        Assert.Equal(0, RelevanceKey.None.Y);
        Assert.Equal(string.Empty, RelevanceKey.None.Group); // the sentinel matches only the empty group
    }

    [Fact]
    public void RelevanceKey_DistanceTo_IsEuclideanAndSymmetric()
    {
        var a = new RelevanceKey(0, 0, "");
        var b = new RelevanceKey(3, 4, ""); // 3-4-5 triangle

        Assert.Equal(5, a.DistanceTo(b));
        Assert.Equal(5, b.DistanceTo(a));
    }

    [Fact]
    public void EntitySnapshot_SizeBytes_ReflectsPayloadLength()
    {
        var entity = new EntitySnapshot(new EntityId("e"), Version: 1, RelevanceKey.None, new byte[7]);

        Assert.Equal(7, entity.SizeBytes);
    }
}
