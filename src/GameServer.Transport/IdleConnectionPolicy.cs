namespace GameServer.Transport;

/// <summary>What the connection loop should do given how long a connection has been quiet.</summary>
public enum IdleAction
{
    /// <summary>Still healthy; keep waiting for input.</summary>
    KeepAlive,

    /// <summary>Quiet past the heartbeat interval; send a server ping to prove liveness.</summary>
    SendHeartbeat,

    /// <summary>Quiet past the idle deadline (a zombie/half-open socket); close it.</summary>
    Disconnect,
}

/// <summary>
/// Decides when an otherwise-silent connection should be pinged or reaped. A client that stops
/// sending (a half-open socket, a frozen device) would otherwise park a server task and its
/// buffers forever — at 100k connections even a tiny fraction of zombies is a standing leak.
/// </summary>
/// <remarks>
/// Partially wired: the connection loop already reaps on <see cref="IdleTimeout"/> (a short
/// pre-hello handshake deadline and a longer post-hello idle deadline — see
/// <c>RealtimeServer.HandleConnectionAsync</c>), pinned by <c>IdleConnectionScenario</c>. Not yet
/// driven: heartbeat liveness — calling <see cref="Evaluate"/> on a timer to emit a server ping
/// (the proto defines ping/pong and <c>DisconnectReason.IdleTimeout</c>) before the idle deadline.
/// </remarks>
public interface IIdleConnectionPolicy
{
    /// <summary>How long a connection may stay quiet before a heartbeat is sent.</summary>
    TimeSpan HeartbeatInterval { get; }

    /// <summary>How long a connection may stay quiet (no inbound, no pong) before it is closed.</summary>
    TimeSpan IdleTimeout { get; }

    /// <summary>
    /// Given time since the last inbound activity and time since the last heartbeat was sent,
    /// decide whether to keep waiting, ping, or disconnect.
    /// </summary>
    IdleAction Evaluate(TimeSpan sinceLastInbound, TimeSpan sinceLastHeartbeat);
}

/// <summary>
/// Heartbeat-then-timeout idle policy: ping once the heartbeat interval elapses with no
/// inbound traffic, disconnect once the idle timeout elapses.
/// </summary>
public sealed class HeartbeatIdlePolicy : IIdleConnectionPolicy
{
    public HeartbeatIdlePolicy(TimeSpan heartbeatInterval, TimeSpan idleTimeout)
    {
        if (heartbeatInterval <= TimeSpan.Zero || idleTimeout <= heartbeatInterval)
        {
            throw new ArgumentException("Idle timeout must be a positive value greater than the heartbeat interval.");
        }

        HeartbeatInterval = heartbeatInterval;
        IdleTimeout = idleTimeout;
    }

    public TimeSpan HeartbeatInterval { get; }

    public TimeSpan IdleTimeout { get; }

    public IdleAction Evaluate(TimeSpan sinceLastInbound, TimeSpan sinceLastHeartbeat)
    {
        // Quiet past the idle deadline (no inbound, no pong): the connection is a zombie — reap it.
        if (sinceLastInbound >= IdleTimeout)
        {
            return IdleAction.Disconnect;
        }

        // Quiet past the heartbeat interval and we have not pinged recently: prove liveness.
        if (sinceLastInbound >= HeartbeatInterval && sinceLastHeartbeat >= HeartbeatInterval)
        {
            return IdleAction.SendHeartbeat;
        }

        return IdleAction.KeepAlive;
    }
}
