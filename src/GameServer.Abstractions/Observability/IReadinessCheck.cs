namespace GameServer.Observability;

/// <summary>The outcome of probing one dependency for readiness.</summary>
/// <param name="Name">The dependency this result is for (e.g. "telemetry", "tenant-resolver").</param>
/// <param name="Ready">True if the dependency can actually do useful work right now.</param>
/// <param name="Detail">A short human-readable reason, surfaced on the readiness endpoint when not ready.</param>
public readonly record struct ReadinessResult(string Name, bool Ready, string Detail)
{
    public static ReadinessResult Healthy(string name) => new(name, true, "ok");
    public static ReadinessResult Unhealthy(string name, string detail) => new(name, false, detail);
}

/// <summary>
/// A readiness contributor: probes one real dependency so <c>/ready</c> proves the node can do
/// useful work, not just that the process is alive (that is <c>/health</c>'s job). Readiness flips
/// to 503 the moment any contributor reports not-ready — e.g. the telemetry sink, tenant resolver,
/// or snapshot store is down — so an orchestrator stops routing traffic to a node that would only
/// fail the requests. Implementations must be CHEAP (a readiness probe is hit frequently and must
/// not itself become a DoS vector): probe a fast invariant, never a full scan.
/// </summary>
public interface IReadinessCheck
{
    /// <summary>The dependency name, used in the readiness payload.</summary>
    string Name { get; }

    /// <summary>Probes the dependency. Must be cheap and must not throw (a throw is treated as not-ready by the caller).</summary>
    ReadinessResult Check();
}
