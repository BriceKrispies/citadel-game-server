namespace GameServer.Routing;

/// <summary>
/// Resolves room affinity by consulting the <see cref="IRoomDirectory"/> and comparing the room's owner
/// to the local node: the owner serves locally, a non-owner redirects to the owner. An unowned room
/// resolves to <see cref="RouteDecision.Local"/> — the local node is eligible to take it, and the
/// serving path claims ownership (so exactly one node wins the claim). Read-only over the directory, so
/// it works unchanged over any backend (in-memory or distributed).
/// </summary>
public sealed class DirectoryRoomAffinityRouter : IRoomAffinityRouter
{
    private readonly IRoomDirectory _directory;
    private readonly NodeId _localNode;

    public DirectoryRoomAffinityRouter(IRoomDirectory directory, NodeId localNode)
    {
        _directory = directory;
        _localNode = localNode;
    }

    public RouteDecision Resolve(RoomKey room)
    {
        // Unowned, or owned by us → serve locally; owned by another node → redirect to that owner.
        if (!_directory.TryGetOwner(room, out var owner) || owner.Equals(_localNode))
        {
            return RouteDecision.Local;
        }

        return RouteDecision.RedirectTo(owner);
    }
}
