namespace GameServer.Replication;

/// <summary>Full snapshot every tick, or delta against the viewer's acknowledged baseline.</summary>
public enum SnapshotMode
{
    Full,
    Delta,
}

/// <summary>Which interest strategy a game uses.</summary>
public enum InterestKind
{
    Everyone,
    Radius,
    Grid,
    Group,
}

/// <summary>
/// Per-game replication configuration, resolved from the control-plane game catalog
/// at room creation. Lets the platform host many games with different needs: a 1v1
/// game uses <see cref="InterestKind.Everyone"/> + <see cref="SnapshotMode.Full"/>
/// (today's behavior), while a 100-player game opts into grid interest, delta, and a
/// per-client byte budget.
/// </summary>
public sealed record ReplicationPolicy(
    SnapshotMode SnapshotMode,
    InterestKind Interest,
    double Radius,
    double CellSize,
    int NeighborRings,
    int PerClientBudgetBytes,
    int SendRateHz)
{
    /// <summary>The conservative default: everyone sees everything, full snapshots, no budget.</summary>
    public static ReplicationPolicy Default { get; } =
        new(SnapshotMode.Full, InterestKind.Everyone, Radius: 0, CellSize: 0, NeighborRings: 0, PerClientBudgetBytes: 0, SendRateHz: 20);

    /// <summary>Builds the interest strategy this policy selects, carrying its parameters.</summary>
    public IInterestStrategy CreateInterestStrategy() => Interest switch
    {
        InterestKind.Everyone => new EveryoneInterest(),
        InterestKind.Radius => new RadiusInterest(Radius),
        InterestKind.Grid => new GridInterest(CellSize, NeighborRings),
        InterestKind.Group => new GroupInterest(),
        _ => new EveryoneInterest(),
    };
}
