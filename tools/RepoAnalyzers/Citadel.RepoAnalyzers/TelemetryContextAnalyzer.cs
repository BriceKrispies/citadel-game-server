using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Citadel.RepoAnalyzers;

/// <summary>
/// CITADEL0010 — telemetry must carry its correlation context. A call to the telemetry sink
/// (default <c>ITelemetrySink.Increment/Measure/Event</c>) for a metric listed in the
/// <c>requiredTagsByMetric</c> schema must include every required tag key. To stay error-safe the
/// rule is conservative: it fires only when the metric is in the schema <em>and</em> the tag
/// argument is a literal-style construction (it contains at least one string-literal key) that is
/// missing a required key. An opaque tag helper (no literal keys) is not second-guessed, and a
/// metric outside the schema is not policed — so the rule never breaks the build on code it cannot
/// prove violates the contract.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TelemetryContextAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "CITADEL0010";

    private static readonly ImmutableHashSet<string> SinkMethods =
        ImmutableHashSet.Create(StringComparer.Ordinal, "Increment", "Measure", "Event");

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Telemetry must carry required context tags",
        messageFormat: "Telemetry for '{0}' is missing required context tag(s): {1}; every emission of "
            + "this metric must be correlatable.",
        category: "Citadel.Observability",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Telemetry calls for metrics in the requiredTagsByMetric schema must include the "
            + "required tag keys. The schema is configured in repo-analyzers.json (invariants).");

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
        if (config.RequiredTagsByMetric.IsEmpty)
        {
            // No schema configured: rule is dormant (never breaks a build on an unpoliced metric).
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

        if (!SinkMethods.Contains(method.Name) ||
            method.ContainingType is null ||
            !string.Equals(method.ContainingType.ToDisplayString(), config.TelemetrySink, StringComparison.Ordinal))
        {
            return;
        }

        var args = node.ArgumentList.Arguments;
        if (args.Count == 0)
        {
            return;
        }

        var metric = MetricKey(args[0].Expression);
        if (metric is null || !config.RequiredTagsByMetric.TryGetValue(metric, out var required))
        {
            return;
        }

        // The tags/fields dictionary is the last argument (Increment(metric, tags),
        // Measure(metric, value, tags), Event(name, fields)).
        var tagsArg = args[args.Count - 1].Expression;
        var presentKeys = LiteralKeys(tagsArg);
        if (presentKeys.Count == 0)
        {
            // Opaque tag construction (a helper, a variable): not literal-analyzable — do not guess.
            return;
        }

        var missing = required.Where(r => !presentKeys.Contains(r)).ToImmutableArray();
        if (missing.Length > 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Rule, node.GetLocation(), metric, string.Join(", ", missing)));
        }
    }

    /// <summary>The metric key: the rightmost identifier of a member access (e.g. the constant name) or a literal value.</summary>
    private static string? MetricKey(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
        IdentifierNameSyntax id => id.Identifier.Text,
        LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => literal.Token.ValueText,
        _ => null,
    };

    /// <summary>All string-literal keys reachable in a tag-construction expression (tuple keys, dictionary keys).</summary>
    private static HashSet<string> LiteralKeys(ExpressionSyntax tagsArg)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var literal in tagsArg.DescendantNodesAndSelf().OfType<LiteralExpressionSyntax>())
        {
            if (literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                keys.Add(literal.Token.ValueText);
            }
        }

        return keys;
    }
}
