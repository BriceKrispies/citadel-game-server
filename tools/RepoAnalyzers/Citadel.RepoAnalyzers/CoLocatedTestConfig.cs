using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace Citadel.RepoAnalyzers;

/// <summary>
/// Configuration for the co-located test rule, loaded from <c>repo-analyzers.json</c>.
/// The ignore system is intentionally open-ended: new ignore categories can be added
/// here and to the JSON without changing the rule logic.
/// </summary>
internal sealed class CoLocatedTestConfig
{
    public const string FileName = "repo-analyzers.json";

    public string RequiredTestSuffix { get; private set; } = ".Tests.cs";

    public bool IgnoreGeneratedFiles { get; private set; } = true;

    /// <summary>
    /// When true, a production file is exempt from the co-located-test requirement if it
    /// declares no executable behavior — i.e. its syntax tree contains zero statements.
    /// This covers pure contract files: interfaces, enums, DTO/value records, const
    /// vocabularies, no-op null objects, and thin wrappers whose members are all
    /// signatures, fields, constants, auto-properties, or expression-bodied accessors.
    /// Such files have nothing to unit-test, so demanding a co-located test produces only
    /// vacuous assertions. Defaults to false so the rule stays strict unless opted in.
    /// </summary>
    public bool IgnoreDeclarationOnlyFiles { get; private set; } = false;

    public ImmutableArray<string> IgnoredFileSuffixes { get; private set; } = ImmutableArray.Create(
        ".Tests.cs", ".Test.cs", ".Fakes.cs", ".TestData.cs", ".TestDoubles.cs");

    public ImmutableArray<string> IgnoredPathGlobs { get; private set; } = ImmutableArray.Create(
        "**/bin/**", "**/obj/**", "**/Testing/**", "**/Migrations/**");

    /// <summary>The hard-coded defaults, used when no config file is present.</summary>
    public static CoLocatedTestConfig Default => new();

    /// <summary>
    /// Parses config JSON. Tolerant and dependency-free: each key falls back to its
    /// default when absent or unparseable, so a malformed file never crashes a build.
    /// </summary>
    public static CoLocatedTestConfig Parse(string json)
    {
        var config = new CoLocatedTestConfig();
        if (string.IsNullOrWhiteSpace(json))
        {
            return config;
        }

        var suffix = MatchString(json, "requiredTestSuffix");
        if (suffix is not null)
        {
            config.RequiredTestSuffix = suffix;
        }

        var generated = MatchBool(json, "ignoredGeneratedFiles");
        if (generated.HasValue)
        {
            config.IgnoreGeneratedFiles = generated.Value;
        }

        var declarationOnly = MatchBool(json, "ignoreDeclarationOnlyFiles");
        if (declarationOnly.HasValue)
        {
            config.IgnoreDeclarationOnlyFiles = declarationOnly.Value;
        }

        var suffixes = MatchStringArray(json, "ignoredFileSuffixes");
        if (suffixes is not null)
        {
            config.IgnoredFileSuffixes = suffixes.Value;
        }

        var globs = MatchStringArray(json, "ignoredPathGlobs");
        if (globs is not null)
        {
            config.IgnoredPathGlobs = globs.Value;
        }

        return config;
    }

    private static string? MatchString(string json, string key)
    {
        var match = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]*)\"");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static bool? MatchBool(string json, string key)
    {
        var match = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(true|false)");
        return match.Success ? match.Groups[1].Value == "true" : null;
    }

    private static ImmutableArray<string>? MatchStringArray(string json, string key)
    {
        var match = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\\[([^\\]]*)\\]");
        if (!match.Success)
        {
            return null;
        }

        var items = new List<string>();
        foreach (Match item in Regex.Matches(match.Groups[1].Value, "\"([^\"]*)\""))
        {
            items.Add(item.Groups[1].Value);
        }

        return items.ToImmutableArray();
    }
}
