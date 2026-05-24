namespace GameServer.Routing;

/// <summary>Whether a connection for a room should be served on the local node or redirected to its owner.</summary>
public enum RouteKind
{
    /// <summary>This node owns the room; serve the connection here.</summary>
    Local,

    /// <summary>Another node owns the room; the connection must be sent to <see cref="RouteDecision.Owner"/>.</summary>
    Redirect,
}

/// <summary>The routing decision for a connection: serve it locally, or redirect it to the owning node.</summary>
public readonly record struct RouteDecision(RouteKind Kind, NodeId Owner)
{
    /// <summary>The room is owned locally; serve here.</summary>
    public static RouteDecision Local { get; } = new(RouteKind.Local, default);

    /// <summary>The room is owned by <paramref name="owner"/>; redirect there.</summary>
    public static RouteDecision RedirectTo(NodeId owner) => new(RouteKind.Redirect, owner);
}

/// <summary>
/// Resolves where a connection for a given room must be handled, relative to the local node: served
/// here when this node owns the room, or redirected to the owning node otherwise. This is the edge
/// piece that turns a fleet's room-agnostic load balancing into room affinity, so a non-owner never
/// serves a room — which is exactly how split brain happens today (a connection lands on any node and
/// that node creates its own copy of the room).
/// </summary>
public interface IRoomAffinityRouter
{
    /// <summary>Decides how a connection for <paramref name="room"/> should be routed from the local node.</summary>
    RouteDecision Resolve(RoomKey room);
}
