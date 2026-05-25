using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace Citadel.RepoAnalyzers;

/// <summary>
/// Configuration for the architectural-invariant rules (CITADEL0004–0013), loaded from the
/// <c>"invariants"</c> section of <c>repo-analyzers.json</c>. The values are <em>derived from
/// the surface manifest</em> (<c>docs/manifest/surface-manifest.json</c> →
/// <c>invariantEvidence</c>): the manifest records, from real traced code, which symbols carry
/// each invariant, and this config is how those facts reach the analyzers.
///
/// Every field has a baked-in default equal to the manifest-derived value, so the rules work
/// even before the JSON block exists. Parsing is tolerant and dependency-free (no JSON library
/// on netstandard2.0): only the string-array tuning knobs (assemblies, allowlists) are read
/// from JSON; anything absent falls back to the default.
/// </summary>
internal sealed class InvariantConfig
{
    public const string FileName = "repo-analyzers.json";

    // ---- CITADEL0004 simulation purity ------------------------------------------------------
    public ImmutableHashSet<string> SimulationAssemblies { get; private set; } =
        ImmutableHashSet.Create(StringComparer.Ordinal, "GameServer.Simulation");

    /// <summary>Banned "<c>Namespace.Type.Member</c>" accessors/methods inside the simulation kernel.</summary>
    public ImmutableHashSet<string> SimulationBannedMembers { get; private set; } =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "System.DateTime.Now",
            "System.DateTime.UtcNow",
            "System.DateTime.Today",
            "System.DateTimeOffset.Now",
            "System.DateTimeOffset.UtcNow",
            "System.Random.Shared",
            "System.Guid.NewGuid",
            "System.Environment.TickCount",
            "System.Environment.TickCount64",
            "System.Threading.Thread.Sleep",
            "System.Threading.Tasks.Task.Delay");

    /// <summary>Types whose every use is banned in the simulation kernel (wall-clock instruments).</summary>
    public ImmutableHashSet<string> SimulationBannedTypes { get; private set; } =
        ImmutableHashSet.Create(StringComparer.Ordinal, "System.Diagnostics.Stopwatch");

    // ---- CITADEL0005 protocol payload immutability ------------------------------------------
    public string PayloadMarker { get; private set; } = "GameServer.Protocol.IMessagePayload";

    // ---- CITADEL0006 bounded queues ---------------------------------------------------------
    public ImmutableHashSet<string> BoundedQueueAssemblies { get; private set; } =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "GameServer.Transport",
            "GameServer.Routing",
            "GameServer.Cluster.Redis");

    /// <summary>
    /// Types permitted to create an unbounded channel despite living in a bounded-queue assembly.
    /// Seeded with the in-process test transport (<c>InMemoryBidirectionalTransport</c>), the one
    /// production-compiled exception the trace found. New hot-path code is still held to bounds.
    /// </summary>
    public ImmutableHashSet<string> UnboundedChannelAllowlist { get; private set; } =
        ImmutableHashSet.Create(StringComparer.Ordinal, "InMemoryBidirectionalTransport");

    // ---- CITADEL0007 tenant context with room identity --------------------------------------
    public ImmutableHashSet<string> TenantScopedAssemblies { get; private set; } =
        ImmutableHashSet.Create(StringComparer.Ordinal, "GameServer.Transport", "GameServer.Routing");

    public string RoomIdTypeName { get; private set; } = "RoomId";

    /// <summary>Parameter types that satisfy "carries tenant context" alongside a <c>RoomId</c>.</summary>
    public ImmutableHashSet<string> TenantParamTypes { get; private set; } =
        ImmutableHashSet.Create(StringComparer.Ordinal, "TenantId", "TenantContext", "RoomKey", "Session");

    /// <summary>
    /// Types exempt from the tenant-context rule. Seeded with the cross-process room proxy and its
    /// exception: the IPC envelope is keyed by RoomId alone (the manifest's known
    /// <c>ipc-no-tenant-tag</c> gap), tracked for follow-up rather than refactored under this change.
    /// </summary>
    public ImmutableHashSet<string> TenantContextExemptTypes { get; private set; } =
        ImmutableHashSet.Create(StringComparer.Ordinal, "RemoteGameRoom", "RemoteRoomException");

    // ---- CITADEL0008 dispatch exhaustiveness ------------------------------------------------
    public string DispatchEnum { get; private set; } = "GameServer.Protocol.MessageType";

    // ---- CITADEL0009 room-mutation ownership ------------------------------------------------
    public string SimulationInterface { get; private set; } = "GameServer.Simulation.IGameSimulation";
    public string MutationMethod { get; private set; } = "Apply";
    public string MutationOwnerType { get; private set; } = "GameRoom";

    // ---- CITADEL0010 telemetry context tags -------------------------------------------------
    public string TelemetrySink { get; private set; } = "GameServer.Observability.ITelemetrySink";

    /// <summary>
    /// Metric/event name → required tag keys. Seeded conservatively with the correlation-complete
    /// call sites the trace confirmed; expand as the untagged hot-path gaps are closed. A metric not
    /// listed here is not policed, so the rule never breaks the build on a metric outside the schema.
    /// </summary>
    public ImmutableDictionary<string, ImmutableArray<string>> RequiredTagsByMetric { get; private set; } =
        ImmutableDictionary<string, ImmutableArray<string>>.Empty;

    // ---- CITADEL0011/0012/0013 data-flow ----------------------------------------------------
    /// <summary>Identifier names treated as the trusted tenant source (provenance must trace here).</summary>
    public ImmutableHashSet<string> TrustedTenantSources { get; private set; } =
        ImmutableHashSet.Create(StringComparer.Ordinal, "session", "connection", "principal");

    /// <summary>Identifier names treated as untrusted inbound/wire input.</summary>
    public ImmutableHashSet<string> UntrustedInboundNames { get; private set; } =
        ImmutableHashSet.Create(StringComparer.Ordinal, "inbound", "envelope", "message", "wire", "request");

    public bool HasValue { get; private set; } = true;

    public static InvariantConfig Default => new();

    /// <summary>Loads the config from the <c>repo-analyzers.json</c> AdditionalFile, or the defaults.</summary>
    public static InvariantConfig Load(ImmutableArray<AdditionalText> additionalFiles, CancellationToken cancellationToken)
    {
        foreach (var file in additionalFiles)
        {
            if (string.IsNullOrEmpty(file.Path))
            {
                continue;
            }

            var normalized = file.Path.Replace('\\', '/');
            var slash = normalized.LastIndexOf('/');
            var name = slash >= 0 ? normalized.Substring(slash + 1) : normalized;
            if (!string.Equals(name, FileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = file.GetText(cancellationToken);
            return text is null ? Default : Parse(text.ToString());
        }

        return Default;
    }

    public static InvariantConfig Parse(string json)
    {
        var config = new InvariantConfig();
        if (string.IsNullOrWhiteSpace(json))
        {
            return config;
        }

        // Only override list/scalar knobs that ops legitimately tune; the manifest-derived
        // identity of each symbol stays in the defaults. Absent keys keep their default.
        Override(json, "simulationAssemblies", v => config.SimulationAssemblies = v);
        Override(json, "simulationBannedMembers", v => config.SimulationBannedMembers = v);
        Override(json, "simulationBannedTypes", v => config.SimulationBannedTypes = v);
        Override(json, "boundedQueueAssemblies", v => config.BoundedQueueAssemblies = v);
        Override(json, "unboundedChannelAllowlist", v => config.UnboundedChannelAllowlist = v);
        Override(json, "tenantScopedAssemblies", v => config.TenantScopedAssemblies = v);
        Override(json, "tenantParamTypes", v => config.TenantParamTypes = v);
        Override(json, "tenantContextExemptTypes", v => config.TenantContextExemptTypes = v);
        Override(json, "trustedTenantSources", v => config.TrustedTenantSources = v);
        Override(json, "untrustedInboundNames", v => config.UntrustedInboundNames = v);

        var marker = MatchString(json, "protocolPayloadMarker");
        if (marker is not null) config.PayloadMarker = marker;
        var dispatch = MatchString(json, "dispatchEnum");
        if (dispatch is not null) config.DispatchEnum = dispatch;
        var roomId = MatchString(json, "roomIdType");
        if (roomId is not null) config.RoomIdTypeName = roomId;
        var owner = MatchString(json, "mutationOwnerType");
        if (owner is not null) config.MutationOwnerType = owner;
        var sink = MatchString(json, "telemetrySink");
        if (sink is not null) config.TelemetrySink = sink;

        config.RequiredTagsByMetric = ParseRequiredTags(json);
        return config;
    }

    private static void Override(string json, string key, Action<ImmutableHashSet<string>> set)
    {
        var values = MatchStringArray(json, key);
        if (values.Count > 0)
        {
            set(values.ToImmutableHashSet(StringComparer.Ordinal));
        }
    }

    /// <summary>
    /// Parses <c>"requiredTagsByMetric"</c> entries encoded flat as <c>"Metric=tagA,tagB"</c>
    /// strings — a netstandard2.0-friendly shape that avoids a nested-object JSON parser.
    /// </summary>
    private static ImmutableDictionary<string, ImmutableArray<string>> ParseRequiredTags(string json)
    {
        var entries = MatchStringArray(json, "requiredTagsByMetric");
        if (entries.Count == 0)
        {
            return ImmutableDictionary<string, ImmutableArray<string>>.Empty;
        }

        var builder = ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var eq = entry.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var metric = entry.Substring(0, eq).Trim();
            var tags = entry.Substring(eq + 1)
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            var tagBuilder = ImmutableArray.CreateBuilder<string>();
            foreach (var tag in tags)
            {
                var trimmed = tag.Trim();
                if (trimmed.Length > 0)
                {
                    tagBuilder.Add(trimmed);
                }
            }

            if (metric.Length > 0 && tagBuilder.Count > 0)
            {
                builder[metric] = tagBuilder.ToImmutable();
            }
        }

        return builder.ToImmutable();
    }

    private static string? MatchString(string json, string key)
    {
        var match = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]*)\"");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static List<string> MatchStringArray(string json, string key)
    {
        var result = new List<string>();
        var match = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\\[([^\\]]*)\\]");
        if (!match.Success)
        {
            return result;
        }

        foreach (Match item in Regex.Matches(match.Groups[1].Value, "\"([^\"]*)\""))
        {
            result.Add(item.Groups[1].Value);
        }

        return result;
    }
}
