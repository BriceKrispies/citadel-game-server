namespace GameServer.Replication;

/// <summary>
/// Interest management / area-of-interest: decides which entities are relevant to a
/// viewer. This is the technique that breaks the O(N²) fan-out — a viewer receives
/// only the k entities relevant to it, not all N. Pluggable per game.
/// </summary>
public interface IInterestStrategy
{
    IReadOnlyList<EntitySnapshot> Relevant(Viewer viewer, IReadOnlyList<EntitySnapshot> world);
}

/// <summary>No filtering — every viewer sees every entity. Correct for small rooms / 1v1 / turn-based.</summary>
public sealed class EveryoneInterest : IInterestStrategy
{
    public IReadOnlyList<EntitySnapshot> Relevant(Viewer viewer, IReadOnlyList<EntitySnapshot> world) => world;
}

/// <summary>Spatial radius: entities within (inclusive) <see cref="Radius"/> of the viewer's key.</summary>
public sealed class RadiusInterest : IInterestStrategy
{
    public RadiusInterest(double radius) => Radius = radius;

    public double Radius { get; }

    public IReadOnlyList<EntitySnapshot> Relevant(Viewer viewer, IReadOnlyList<EntitySnapshot> world) =>
        world.Where(e => viewer.Key.DistanceTo(e.Key) <= Radius).ToList();
}

/// <summary>Spatial grid: entities in the viewer's cell and the surrounding <see cref="NeighborRings"/> rings.</summary>
public sealed class GridInterest : IInterestStrategy
{
    public GridInterest(double cellSize, int neighborRings)
    {
        CellSize = cellSize;
        NeighborRings = neighborRings;
    }

    public double CellSize { get; }
    public int NeighborRings { get; }

    public IReadOnlyList<EntitySnapshot> Relevant(Viewer viewer, IReadOnlyList<EntitySnapshot> world)
    {
        var viewerCol = Cell(viewer.Key.X);
        var viewerRow = Cell(viewer.Key.Y);

        return world.Where(e =>
        {
            // King-move (Chebyshev) distance in cells: own cell is ring 0.
            var rings = Math.Max(Math.Abs(Cell(e.Key.X) - viewerCol), Math.Abs(Cell(e.Key.Y) - viewerRow));
            return rings <= NeighborRings;
        }).ToList();
    }

    // floor (not truncation) so negative coordinates map to the correct cell.
    private long Cell(double coordinate) => (long)Math.Floor(coordinate / CellSize);
}

/// <summary>Group/subscription: entities whose key group matches the viewer's group.</summary>
public sealed class GroupInterest : IInterestStrategy
{
    public IReadOnlyList<EntitySnapshot> Relevant(Viewer viewer, IReadOnlyList<EntitySnapshot> world) =>
        world.Where(e => string.Equals(e.Key.Group, viewer.Key.Group, StringComparison.Ordinal)).ToList();
}
