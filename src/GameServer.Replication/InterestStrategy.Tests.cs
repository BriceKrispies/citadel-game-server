using Xunit;

namespace GameServer.Replication;

public sealed class InterestStrategyTests
{
    private static EntitySnapshot Entity(string id, double x, double y, string group = "") =>
        new(new EntityId(id), Version: 1, new RelevanceKey(x, y, group), Payload: new byte[4]);

    private static string[] Ids(IEnumerable<EntitySnapshot> entities) =>
        entities.Select(e => e.Id.Value).OrderBy(v => v).ToArray();

    [Fact]
    public void Interest_Everyone_ReturnsAllEntitiesWithoutDuplication()
    {
        var world = new[] { Entity("a", 0, 0), Entity("b", 1000, 1000), Entity("c", -5, 5) };
        var viewer = new Viewer(new ViewerId("v"), new RelevanceKey(0, 0, ""));

        var relevant = new EveryoneInterest().Relevant(viewer, world);

        Assert.Equal(3, relevant.Count);                  // no drops, no duplication
        Assert.Equal(new[] { "a", "b", "c" }, Ids(relevant));
    }

    [Fact]
    public void Interest_Radius_IsInclusiveAtBoundary_AndUsesBothAxes()
    {
        // viewer at origin, radius 10. (6,8) is at distance exactly 10 (must be IN);
        // (7,8) is ~10.63 (OUT). Both use the y-axis, so a bug ignoring dy is caught.
        var world = new[]
        {
            Entity("origin", 0, 0),
            Entity("boundary", 6, 8),   // distance == 10
            Entity("justBeyond", 7, 8), // distance ~10.63
            Entity("far", 15, 0),       // distance 15
        };
        var viewer = new Viewer(new ViewerId("v"), new RelevanceKey(0, 0, ""));

        var relevant = new RadiusInterest(radius: 10).Relevant(viewer, world);

        Assert.Equal(new[] { "boundary", "origin" }, Ids(relevant));
    }

    [Fact]
    public void Interest_Grid_IncludesChebyshevNeighbors_ExcludesFartherAndTruncationTraps()
    {
        // cellSize 10, rings 1, viewer in cell (0,0).
        var world = new[]
        {
            Entity("same", 5, 5),       // cell (0,0)
            Entity("axis", 15, 5),      // cell (1,0)  Chebyshev 1 -> IN
            Entity("diagonal", 15, 15), // cell (1,1)  Chebyshev 1 -> IN (Manhattan would wrongly exclude)
            Entity("twoCells", 25, 5),  // cell (2,0)  Chebyshev 2 -> OUT
            Entity("negFar", -15, 5),   // floor(-1.5) = -2 -> cell (-2,0) Chebyshev 2 -> OUT
                                        // (truncation toward zero would give cell (-1,0) -> wrongly IN)
        };
        var viewer = new Viewer(new ViewerId("v"), new RelevanceKey(5, 5, ""));

        var relevant = new GridInterest(cellSize: 10, neighborRings: 1).Relevant(viewer, world);

        Assert.Equal(new[] { "axis", "diagonal", "same" }, Ids(relevant));
    }

    [Fact]
    public void Interest_Grid_ZeroRings_ReturnsOwnCellOnly()
    {
        var world = new[] { Entity("same", 5, 5), Entity("neighbor", 15, 5) };
        var viewer = new Viewer(new ViewerId("v"), new RelevanceKey(5, 5, ""));

        var relevant = new GridInterest(cellSize: 10, neighborRings: 0).Relevant(viewer, world);

        Assert.Equal(new[] { "same" }, Ids(relevant));
    }

    [Fact]
    public void Interest_Group_ReturnsOnlySameGroupRegardlessOfPosition()
    {
        var world = new[] { Entity("r1", 0, 0, "red"), Entity("r2", 99, 99, "red"), Entity("b1", 0, 0, "blue") };
        var viewer = new Viewer(new ViewerId("v"), new RelevanceKey(0, 0, "red"));

        var relevant = new GroupInterest().Relevant(viewer, world);

        Assert.Equal(new[] { "r1", "r2" }, Ids(relevant)); // co-located blue is excluded; far red is included
    }

    [Fact]
    public void Interest_Radius_WorksFromNonOriginViewer_UsingSignedDeltas()
    {
        // Viewer NOT at the origin: distance must use signed deltas (X - other.X),
        // not |X| + |other.X|. A 6-8-10 triangle gives an exact boundary hit.
        var viewer = new Viewer(new ViewerId("v"), new RelevanceKey(10, 10, ""));
        var world = new[]
        {
            Entity("self", 10, 10),  // distance 0
            Entity("near", 16, 18),  // dx 6, dy 8 -> distance 10 (inclusive -> IN)
            Entity("far", 17, 18),   // ~10.63 -> OUT
        };

        var relevant = new RadiusInterest(radius: 10).Relevant(viewer, world);

        Assert.Equal(new[] { "near", "self" }, Ids(relevant));
    }

    [Fact]
    public void Interest_Grid_WorksFromNonOriginViewerCell()
    {
        // Viewer in cell (3,3): cell distance must subtract the viewer cell, not add it.
        var viewer = new Viewer(new ViewerId("v"), new RelevanceKey(35, 35, ""));
        var world = new[]
        {
            Entity("same", 35, 35),     // cell (3,3) ring 0
            Entity("axis", 45, 35),     // cell (4,3) ring 1
            Entity("diagonal", 25, 25), // cell (2,2) ring 1
            Entity("twoCells", 55, 35), // cell (5,3) ring 2 -> OUT
        };

        var relevant = new GridInterest(cellSize: 10, neighborRings: 1).Relevant(viewer, world);

        Assert.Equal(new[] { "axis", "diagonal", "same" }, Ids(relevant));
    }
}
