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
/// Decides when an otherwise-silent connection should be pinged or reaped. Today the
/// connection loop <c>await</c>s <see cref="IClientCommandReceiver.ReceiveAsync"/> with no
/// deadline, so a client that stops sending (a half-open socket, a frozen device) parks a
/// server task and its buffers forever — at 100k connections even a tiny fraction of zombies
/// is a standing leak. A hardened loop consults this policy on a timer: ping at the heartbeat
/// interval, and close with <c>DisconnectReason.IdleTimeout</c> once the idle deadline passes.
/// </summary>
/// <remarks>
/// RED-phase seam: the contract exists so the idle-reaping behavior can be pinned by a test
/// (<c>IdleConnectionScenario</c>); the evaluation and the loop's use of it are not built yet.
/// The wire protocol already defines <c>DisconnectReason.IdleTimeout</c> and ping/pong; nothing
/// drives them.
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
