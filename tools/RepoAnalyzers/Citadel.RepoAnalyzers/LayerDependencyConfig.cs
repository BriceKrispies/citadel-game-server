using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace Citadel.RepoAnalyzers;

/// <summary>
/// Configuration for the layer-dependency rule (CITADEL0002), loaded from the
/// <c>"layering"</c> section of <c>repo-analyzers.json</c>.
///
/// The model is a concentric ring architecture: each <c>GameServer.*</c> assembly is
/// assigned an integer rank. Rank <see cref="UniversalRank"/> is the shared kernel and
/// may be referenced from any ring. For every other rank, only the immediately inner
/// rank (strict adjacency) may be referenced. Composition roots are exempt because they
/// wire concrete implementations from every layer.
///
/// Parsing is tolerant and dependency-free (no JSON library on netstandard2.0): when the
/// <c>"layering"</c> section is absent the rule is dormant, so the analyzer ships safely
/// before the ranks are populated.
/// </summary>
internal sealed class LayerDependencyConfig
{
    public const string FileName = "repo-analyzers.json";

    /// <summary>The rank treated as the universal shared kernel (default 0).</summary>
    public int UniversalRank { get; private set; }

    /// <summary>Assembly-name (e.g. <c>GameServer.Simulation</c>) to rank.</summary>
    public ImmutableDictionary<string, int> Layers { get; private set; } =
        ImmutableDictionary<string, int>.Empty;

    /// <summary>Assemblies exempt from the rule (composition roots that wire all layers).</summary>
    public ImmutableHashSet<string> CompositionRoots { get; private set; } =
        ImmutableHashSet<string>.Empty;

    /// <summary>True when a usable layering map was found; otherwise the rule is dormant.</summary>
    public bool HasLayering => !Layers.IsEmpty;

    public static LayerDependencyConfig Default => new();

    public static LayerDependencyConfig Parse(string json)
    {
        var config = new LayerDependencyConfig();
        if (string.IsNullOrWhiteSpace(json))
        {
            return config;
        }

        var universal = MatchInt(json, "universalRank");
        if (universal.HasValue)
        {
            config.UniversalRank = universal.Value;
        }

        // Every "GameServer.X": <int> pair is a layer assignment. Composition roots appear
        // inside a string array ("GameServer.Host" with no ': <int>'), so they never match.
        var layers = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
        foreach (Match pair in Regex.Matches(json, "\"(GameServer\\.[^\"]+)\"\\s*:\\s*(\\d+)"))
        {
            layers[pair.Groups[1].Value] = int.Parse(pair.Groups[2].Value);
        }

        config.Layers = layers.ToImmutable();
        config.CompositionRoots = MatchStringArray(json, "compositionRoots")
            .ToImmutableHashSet(StringComparer.Ordinal);
        return config;
    }

    private static int? MatchInt(string json, string key)
    {
        var match = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(\\d+)");
        return match.Success ? int.Parse(match.Groups[1].Value) : (int?)null;
    }

    private static IEnumerable<string> MatchStringArray(string json, string key)
    {
        var match = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\\[([^\\]]*)\\]");
        if (!match.Success)
        {
            yield break;
        }

        foreach (Match item in Regex.Matches(match.Groups[1].Value, "\"([^\"]*)\""))
        {
            yield return item.Groups[1].Value;
        }
    }
}
