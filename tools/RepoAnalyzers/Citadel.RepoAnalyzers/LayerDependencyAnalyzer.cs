using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Citadel.RepoAnalyzers;

/// <summary>
/// Enforces Citadel's concentric layered architecture (kernel → core → rings).
///
/// Each <c>GameServer.*</c> assembly is ranked in <c>repo-analyzers.json</c>
/// (<c>layering</c>). A compilation may <em>use a symbol</em> from another
/// <c>GameServer.*</c> assembly only when that assembly is the universal kernel
/// (<c>universalRank</c>) or sits exactly one rank inward (strict adjacency). Reaching
/// outward, sideways, or skipping a ring is a build error (CITADEL0002). A
/// <c>GameServer.*</c> assembly with no rank and not listed as a composition root is
/// itself an error (CITADEL0003) so every project must be classified.
///
/// Detection is by used symbol (not by project reference), so it also catches coupling
/// that leaks transitively through an otherwise-allowed reference.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LayerDependencyAnalyzer : DiagnosticAnalyzer
{
    public const string LayerViolationId = "CITADEL0002";
    public const string UnmappedProjectId = "CITADEL0003";

    private const string AssemblyPrefix = "GameServer.";

    private static readonly DiagnosticDescriptor LayerRule = new(
        id: LayerViolationId,
        title: "Layer dependency violates the architecture",
        messageFormat: "Layer violation: '{0}' (layer {1}) must not use '{2}' (layer {3}); "
            + "allowed targets are the universal kernel or the adjacent inner layer.",
        category: "Citadel.Architecture",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A project may use symbols only from the universal kernel layer or the "
            + "layer immediately inward of it. Ranks are configured in repo-analyzers.json (layering).",
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    private static readonly DiagnosticDescriptor UnmappedRule = new(
        id: UnmappedProjectId,
        title: "Project has no layer assignment",
        messageFormat: "Project '{0}' has no layer assignment in repo-analyzers.json (layering.layers) "
            + "and is not a composition root; classify it to keep layering enforceable.",
        category: "Citadel.Architecture",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Every GameServer.* project must be ranked in repo-analyzers.json or declared a "
            + "composition root, so the layering rule cannot be silently bypassed by adding a project.",
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(LayerRule, UnmappedRule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(
            GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var config = LoadConfig(context.Options.AdditionalFiles, context.CancellationToken);
        if (!config.HasLayering)
        {
            // Dormant until ranks are configured.
            return;
        }

        var current = context.Compilation.AssemblyName ?? string.Empty;
        if (current.Length == 0 || config.CompositionRoots.Contains(current))
        {
            // Composition roots may wire any layer; non-GameServer assemblies are not governed.
            return;
        }

        if (!config.Layers.TryGetValue(current, out var currentLayer))
        {
            if (current.StartsWith(AssemblyPrefix, StringComparison.Ordinal))
            {
                context.RegisterCompilationEndAction(end =>
                    end.ReportDiagnostic(Diagnostic.Create(UnmappedRule, Location.None, current)));
            }

            return;
        }

        // First offending usage per banned target assembly (one diagnostic per target, low noise).
        var violations = new ConcurrentDictionary<string, (int Layer, Location Location)>(StringComparer.Ordinal);

        context.RegisterSyntaxNodeAction(
            nodeContext =>
            {
                var name = (SimpleNameSyntax)nodeContext.Node;
                var symbol = nodeContext.SemanticModel.GetSymbolInfo(name, nodeContext.CancellationToken).Symbol;
                var target = symbol?.ContainingAssembly?.Name;
                if (target is not { Length: > 0 } || target == current)
                {
                    return;
                }

                if (!config.Layers.TryGetValue(target, out var targetLayer))
                {
                    // Target is the BCL, a NuGet package, or an unranked assembly: not governed here.
                    return;
                }

                if (IsAllowed(currentLayer, targetLayer, config.UniversalRank))
                {
                    return;
                }

                violations.TryAdd(target, (targetLayer, name.GetLocation()));
            },
            SyntaxKind.IdentifierName,
            SyntaxKind.GenericName);

        context.RegisterCompilationEndAction(end =>
        {
            foreach (var violation in violations)
            {
                end.ReportDiagnostic(Diagnostic.Create(
                    LayerRule,
                    violation.Value.Location,
                    current,
                    currentLayer,
                    violation.Key,
                    violation.Value.Layer));
            }
        });
    }

    private static bool IsAllowed(int currentLayer, int targetLayer, int universalRank)
        => targetLayer == universalRank          // the universal shared kernel
        || currentLayer - targetLayer == 1;       // strictly adjacent inner ring

    private static LayerDependencyConfig LoadConfig(
        ImmutableArray<AdditionalText> additionalFiles, CancellationToken cancellationToken)
    {
        foreach (var file in additionalFiles)
        {
            if (string.IsNullOrEmpty(file.Path))
            {
                continue;
            }

            var name = GetFileName(file.Path);
            if (!string.Equals(name, LayerDependencyConfig.FileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = file.GetText(cancellationToken);
            return text is null ? LayerDependencyConfig.Default : LayerDependencyConfig.Parse(text.ToString());
        }

        return LayerDependencyConfig.Default;
    }

    private static string GetFileName(string path)
    {
        var normalized = path.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        return slash >= 0 ? normalized.Substring(slash + 1) : normalized;
    }
}
