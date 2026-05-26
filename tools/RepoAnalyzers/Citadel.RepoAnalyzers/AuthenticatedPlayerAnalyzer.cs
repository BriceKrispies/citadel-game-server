using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Citadel.RepoAnalyzers;

/// <summary>
/// CITADEL0012 — the server acts on authenticated identity, not client-asserted identity. A
/// player identity taken from untrusted wire input (<c>inbound.PlayerId</c>) must not be fed into a
/// state-mutating call (<c>Apply</c> / <c>Enqueue</c> / <c>TryEnqueue</c>); the authenticated
/// <c>connection.Player</c> bound at join is the only legitimate source. Passing an inbound
/// player into a mutation lets a client act as another player.
///
/// Conservative: fires only when an argument to a mutation call is provably <c>&lt;untrusted&gt;.PlayerId</c>.
/// The correct command path uses <c>connection.Player</c>, so it is never flagged.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AuthenticatedPlayerAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "CITADEL0012";

    private const string PlayerMember = "PlayerId";

    private static readonly ImmutableHashSet<string> MutationMethods =
        ImmutableHashSet.Create(StringComparer.Ordinal, "Apply", "Enqueue", "TryEnqueue");

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Mutations must use authenticated player identity",
        messageFormat: "Authoritative-identity violation: '{0}' feeds untrusted '{1}' into a mutation; "
            + "use the authenticated connection player, not the inbound envelope's player.",
        category: "Citadel.Security",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "An inbound player identity must not parameterize a state mutation. Untrusted "
            + "identifier names and mutation methods are configured in repo-analyzers.json (invariants).");

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
        var methodName = node.Expression switch
        {
            MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
            IdentifierNameSyntax id => id.Identifier.Text,
            _ => null,
        };

        if (methodName is null || !MutationMethods.Contains(methodName))
        {
            return;
        }

        foreach (var argument in node.ArgumentList.Arguments)
        {
            if (argument.Expression is MemberAccessExpressionSyntax member &&
                string.Equals(member.Name.Identifier.Text, PlayerMember, StringComparison.Ordinal))
            {
                var root = AnalyzerSyntax.RootIdentifier(member.Expression);
                if (root is not null && config.UntrustedInboundNames.Contains(root))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        Rule, argument.GetLocation(), methodName, member.ToString()));
                }
            }
        }
    }
}
