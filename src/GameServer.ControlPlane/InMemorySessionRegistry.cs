using System.Collections.Concurrent;

namespace GameServer.ControlPlane;

/// <summary>
/// Control-plane session lifecycle (HTTP create/delete). These are control-plane
/// session records, distinct from the realtime data-plane sessions the kernel
/// issues on ClientHello. Session ids come from a deterministic counter.
/// </summary>
public sealed class InMemorySessionRegistry
{
    private readonly ConcurrentDictionary<string, SessionContract> _sessions = new();
    private long _counter;

    public SessionContract Create(CreateSessionRequest request)
    {
        var sessionId = $"control-session-{System.Threading.Interlocked.Increment(ref _counter)}";
        var session = new SessionContract(sessionId, request.TenantId, request.PlayerId, Status: "active");
        _sessions[sessionId] = session;
        return session;
    }

    public bool TryGet(string sessionId, out SessionContract session) => _sessions.TryGetValue(sessionId, out session!);

    public bool Remove(string sessionId) => _sessions.TryRemove(sessionId, out _);
}
