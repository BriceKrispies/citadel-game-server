using Xunit;

namespace GameServer.Replication;

public sealed class ReplicationPolicyTests
{
    [Fact]
    public void ReplicationPolicy_CreatesConfiguredInterestStrategy()
    {
        Assert.IsType<EveryoneInterest>(
            ReplicationPolicy.Default.CreateInterestStrategy());

        Assert.IsType<RadiusInterest>(
            (ReplicationPolicy.Default with { Interest = InterestKind.Radius, Radius = 12 }).CreateInterestStrategy());

        Assert.IsType<GridInterest>(
            (ReplicationPolicy.Default with { Interest = InterestKind.Grid, CellSize = 10, NeighborRings = 1 }).CreateInterestStrategy());

        Assert.IsType<GroupInterest>(
            (ReplicationPolicy.Default with { Interest = InterestKind.Group }).CreateInterestStrategy());
    }

    [Fact]
    public void ReplicationPolicy_RadiusStrategy_UsesConfiguredRadius()
    {
        var policy = ReplicationPolicy.Default with { Interest = InterestKind.Radius, Radius = 42 };

        var strategy = Assert.IsType<RadiusInterest>(policy.CreateInterestStrategy());

        Assert.Equal(42, strategy.Radius);
    }

    [Fact]
    public void ReplicationPolicy_GridStrategy_UsesConfiguredCellSizeAndRings()
    {
        var policy = ReplicationPolicy.Default with { Interest = InterestKind.Grid, CellSize = 16, NeighborRings = 2 };

        var strategy = Assert.IsType<GridInterest>(policy.CreateInterestStrategy());

        Assert.Equal(16, strategy.CellSize);
        Assert.Equal(2, strategy.NeighborRings);
    }
}
