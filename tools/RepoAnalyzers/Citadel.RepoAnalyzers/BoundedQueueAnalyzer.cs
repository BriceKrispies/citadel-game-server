using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Citadel.RepoAnalyzers;

/// <summary>
/// CITADEL0006 — the realtime planes must degrade under overload, not grow without bound. In a
/// bounded-queue assembly (default Transport / Routing / Cluster.Redis) an unbounded channel
/// (<c>System.Threading.Channels.Channel.CreateUnbounded</c>) is banned: an unbounded buffer on a
/// connection or room path turns backpressure into unbounded memory growth. Use a bounded channel
/// with an explicit overflow policy. The one in-process test transport is allowlisted in config.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BoundedQueueAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "CITADEL0006";

    private const string UnboundedFactoryType = "System.Threading.Channels.Channel";
    private const string UnboundedFactoryMethod = "CreateUnbounded";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Realtime buffers must be bounded",
        messageFormat: "Backpressure violation: '{0}' creates an unbounded channel; realtime/routing "
            + "buffers must be bounded so overload sheds instead of growing memory without limit.",
        category: "Citadel.Architecture",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Unbounded channels are banned in the bounded-queue assemblies. Allowlisted types "
            + "and assemblies are configured in repo-analyzers.json (invariants).");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var config = InvariantConfig.Load(context.Options.AdditionalFiles, context.CancellationToken);
        var assembly = context.Compilation.AssemblyName ?? string.Empty;
        if (!config.BoundedQueueAssemblies.Contains(assembly))
        {
            return;
        }

        context.RegisterSyntaxNodeAction(ctx => AnalyzeInvocation(ctx, config), SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context, InvariantConfig config)
    {
        var node = (InvocationExpressionSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(node, context.CancellationToken).Symbol is not IMethodSymbol method)
        {
            return;
        }

        if (!string.Equals(method.Name, UnboundedFactoryMethod, StringComparison.Ordinal))
        {
            return;
        }

        var containing = method.ContainingType?.ConstructedFrom ?? method.ContainingType;
        if (containing is null ||
            !string.Equals(containing.ToDisplayString(), UnboundedFactoryType, StringComparison.Ordinal))
        {
            return;
        }

        var enclosingType = EnclosingTypeName(node);
        if (enclosingType is not null && config.UnboundedChannelAllowlist.Contains(enclosingType))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, node.GetLocation(), enclosingType ?? "<unknown>"));
    }

    private static string? EnclosingTypeName(SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (current is TypeDeclarationSyntax type)
            {
                return type.Identifier.Text;
            }
        }

        return null;
    }
}
