using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Citadel.RepoAnalyzers;

/// <summary>
/// CITADEL0013 — a structured telemetry <em>event</em> emitted while handling an inbound message
/// must be correlatable to that message's trace. Inside a method that takes an inbound message (a
/// parameter named like the wire input, or typed <c>MessageEnvelope</c>), a call to the sink's
/// structured-event method (<c>Event</c>) must either reference the inbound trace (a tag key
/// containing "trace") or be handed the authenticated correlation source (<c>connection</c> /
/// <c>session</c> / <c>principal</c>), which carries the trace into a correlation-tag helper.
///
/// Scope and conservatism keep it build-green: aggregate counters (<c>Increment</c>/<c>Measure</c>)
/// are NOT policed — a high-cardinality trace id does not belong on a counter — and an event whose
/// tags are an opaque helper with no literal keys is not second-guessed. The rule fires only on a
/// structured event that builds explicit literal tags, names no trusted correlation source, and
/// omits a trace key.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TracePropagationAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "CITADEL0013";

    private const string EnvelopeTypeName = "MessageEnvelope";
    private const string TraceMarker = "trace";

    // Only the structured-event method is policed. Aggregate counters (Increment/Measure) carry
    // low-cardinality dimensions by design; a per-message trace id does not belong on a counter.
    private static readonly ImmutableHashSet<string> SinkMethods =
        ImmutableHashSet.Create(StringComparer.Ordinal, "Event");

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Inbound-handler telemetry must propagate the trace id",
        messageFormat: "Telemetry in inbound handler '{0}' builds explicit tags without a trace key; "
            + "propagate the inbound trace id so the command is correlatable.",
        category: "Citadel.Observability",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Telemetry emitted while handling an inbound message must include a trace tag. "
            + "Inbound parameter names and the sink are configured in repo-analyzers.json (invariants).");

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
        context.RegisterSyntaxNodeAction(ctx => AnalyzeInvocation(ctx, config), SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context, InvariantConfig config)
    {
        var node = (InvocationExpressionSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(node, context.CancellationToken).Symbol is not IMethodSymbol method)
        {
            return;
        }

        if (!SinkMethods.Contains(method.Name) ||
            method.ContainingType is null ||
            !string.Equals(method.ContainingType.ToDisplayString(), config.TelemetrySink, StringComparison.Ordinal))
        {
            return;
        }

        var (methodNode, handlerName) = EnclosingMethod(node);
        if (methodNode is null || !HandlesInbound(methodNode, config))
        {
            return;
        }

        var args = node.ArgumentList.Arguments;
        if (args.Count == 0)
        {
            return;
        }

        var tagsArg = args[args.Count - 1].Expression;

        // If the tags are built from a trusted correlation source (connection/session/principal),
        // a correlation-tag helper carries the trace id in — do not second-guess it.
        if (tagsArg.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
            .Any(id => config.TrustedTenantSources.Contains(id.Identifier.Text)))
        {
            return;
        }

        var literalKeys = tagsArg.DescendantNodesAndSelf()
            .OfType<LiteralExpressionSyntax>()
            .Where(l => l.IsKind(SyntaxKind.StringLiteralExpression))
            .Select(l => l.Token.ValueText)
            .ToImmutableArray();

        if (literalKeys.Length == 0)
        {
            // Opaque tag construction (a correlated-tags helper): not literal-analyzable — do not guess.
            return;
        }

        if (!literalKeys.Any(k => k.IndexOf(TraceMarker, StringComparison.OrdinalIgnoreCase) >= 0))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, node.GetLocation(), handlerName));
        }
    }

    private static (MethodDeclarationSyntax? Node, string Name) EnclosingMethod(SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (current is MethodDeclarationSyntax method)
            {
                return (method, method.Identifier.Text);
            }
        }

        return (null, "<unknown>");
    }

    private static bool HandlesInbound(MethodDeclarationSyntax method, InvariantConfig config)
    {
        foreach (var parameter in method.ParameterList.Parameters)
        {
            if (config.UntrustedInboundNames.Contains(parameter.Identifier.Text))
            {
                return true;
            }

            var typeName = (parameter.Type as IdentifierNameSyntax)?.Identifier.Text
                ?? (parameter.Type as QualifiedNameSyntax)?.Right.Identifier.Text;
            if (string.Equals(typeName, EnvelopeTypeName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
