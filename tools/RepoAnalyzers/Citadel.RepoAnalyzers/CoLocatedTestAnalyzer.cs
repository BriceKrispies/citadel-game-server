using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Citadel.RepoAnalyzers;

/// <summary>
/// Enforces the repository's co-located test convention: every production
/// <c>.cs</c> file must have a sibling test file (by default <c>Name.Tests.cs</c>).
/// Ignore categories are configured via <c>repo-analyzers.json</c> and are
/// open-ended — new categories can be added without changing this rule.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CoLocatedTestAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "CITADEL0001";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Production file is missing a co-located test file",
        messageFormat: "Production file '{0}' does not have a co-located test file '{1}'.",
        category: "Citadel.Testing",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Production .cs files must have a co-located test file. " +
            "Ignored categories (suffixes, path globs, generated files) are configured in repo-analyzers.json.",
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    private static readonly string[] GeneratedNameSuffixes =
    {
        ".g.cs", ".g.i.cs", ".designer.cs", ".generated.cs",
    };

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();

        // Take full control of generated-code handling: Roslyn would otherwise
        // auto-suppress diagnostics on files it considers generated (e.g. *.g.cs).
        // We want repo-analyzers.json ('ignoredGeneratedFiles') to be the single
        // source of truth, so we opt in to analyzing and reporting on generated code
        // and apply our own gate in IsGenerated.
        context.ConfigureGeneratedCodeAnalysis(
            GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.RegisterCompilationAction(Analyze);
    }

    private static void Analyze(CompilationAnalysisContext context)
    {
        var config = LoadConfig(context.Options.AdditionalFiles, context.CancellationToken);

        // Every file path the build knows about: production trees + additional files
        // (the co-located test files are passed to the analyzer as AdditionalFiles).
        var knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tree in context.Compilation.SyntaxTrees)
        {
            if (!string.IsNullOrEmpty(tree.FilePath))
            {
                knownPaths.Add(GlobMatcher.Normalize(tree.FilePath));
            }
        }

        foreach (var additional in context.Options.AdditionalFiles)
        {
            if (!string.IsNullOrEmpty(additional.Path))
            {
                knownPaths.Add(GlobMatcher.Normalize(additional.Path));
            }
        }

        foreach (var tree in context.Compilation.SyntaxTrees)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var path = tree.FilePath;
            if (string.IsNullOrEmpty(path) || IsIgnored(path, tree, config, context.CancellationToken))
            {
                continue;
            }

            var expected = ExpectedTestPath(path, config.RequiredTestSuffix);
            if (knownPaths.Contains(GlobMatcher.Normalize(expected)))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                Rule,
                Location.Create(tree, new TextSpan(0, 0)),
                GetFileName(path),
                GetFileName(expected)));
        }
    }

    private static bool IsIgnored(string path, SyntaxTree tree, CoLocatedTestConfig config, CancellationToken cancellationToken)
    {
        var normalized = GlobMatcher.Normalize(path);
        var fileName = GetFileName(normalized);

        foreach (var suffix in config.IgnoredFileSuffixes)
        {
            if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (config.IgnoreGeneratedFiles && IsGenerated(fileName, tree, cancellationToken))
        {
            return true;
        }

        if (config.IgnoreDeclarationOnlyFiles && IsDeclarationOnly(tree, cancellationToken))
        {
            return true;
        }

        foreach (var glob in config.IgnoredPathGlobs)
        {
            if (GlobMatcher.IsMatch(normalized, glob))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsGenerated(string fileName, SyntaxTree tree, CancellationToken cancellationToken)
    {
        foreach (var suffix in GeneratedNameSuffixes)
        {
            if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var text = tree.GetText(cancellationToken);
        var headLength = Math.Min(text.Length, 500);
        if (headLength == 0)
        {
            return false;
        }

        var head = text.GetSubText(new TextSpan(0, headLength)).ToString();
        return head.IndexOf("<auto-generated", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// True when the file declares no executable behavior. Interfaces, enums, DTO/value
    /// records, const vocabularies, no-op null objects, and expression-bodied accessors all
    /// qualify — their members are signatures, fields, constants, auto-properties, single
    /// expressions, or empty bodies, none of which executes a real statement. Any actual
    /// statement (an assignment, a return, control flow, a multi-step method) makes the file
    /// non-declaration-only and keeps the co-located-test requirement in force.
    /// </summary>
    /// <remarks>
    /// A method's <c>{ }</c> body is a <see cref="BlockSyntax"/>, which is itself a
    /// <see cref="StatementSyntax"/>; we skip the block container and require a genuine
    /// inner statement so that an empty no-op body (e.g. a null-object sink) stays exempt.
    /// </remarks>
    private static bool IsDeclarationOnly(SyntaxTree tree, CancellationToken cancellationToken)
    {
        var root = tree.GetRoot(cancellationToken);
        foreach (var node in root.DescendantNodes())
        {
            if (node is StatementSyntax and not BlockSyntax)
            {
                return false;
            }
        }

        return true;
    }

    private static string ExpectedTestPath(string productionPath, string requiredTestSuffix)
    {
        // Strip the trailing ".cs" and append the configured suffix (e.g. ".Tests.cs").
        var baseName = productionPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            ? productionPath.Substring(0, productionPath.Length - 3)
            : productionPath;
        return baseName + requiredTestSuffix;
    }

    private static string GetFileName(string path)
    {
        var normalized = GlobMatcher.Normalize(path);
        var slash = normalized.LastIndexOf('/');
        return slash >= 0 ? normalized.Substring(slash + 1) : normalized;
    }

    private static CoLocatedTestConfig LoadConfig(ImmutableArray<AdditionalText> additionalFiles, CancellationToken cancellationToken)
    {
        foreach (var file in additionalFiles)
        {
            if (string.IsNullOrEmpty(file.Path))
            {
                continue;
            }

            var name = GetFileName(file.Path);
            if (!string.Equals(name, CoLocatedTestConfig.FileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = file.GetText(cancellationToken);
            return text is null ? CoLocatedTestConfig.Default : CoLocatedTestConfig.Parse(text.ToString());
        }

        return CoLocatedTestConfig.Default;
    }
}
