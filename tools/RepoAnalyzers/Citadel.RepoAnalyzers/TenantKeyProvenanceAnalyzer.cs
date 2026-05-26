using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Citadel.RepoAnalyzers;

/// <summary>
/// CITADEL0011 — a room lookup key must derive its tenant from the trusted, authenticated context,
/// never from untrusted wire input. When a <c>RoomKey</c> is constructed, its tenant argument must
/// not trace to an inbound/wire/envelope value (which a client controls); it must come from the
/// session/connection/principal. Building a key from <c>inbound.TenantId</c> is the canonical
/// cross-tenant escalation bug.
///
/// Data-flow is conservative: the rule fires only when the tenant argument is <em>provably</em>
/// rooted in an untrusted identifier (e.g. <c>inbound.TenantId</c>). Anything ambiguous is allowed,
/// so correct code (which builds keys from <c>session.Tenant…</c>) is never flagged.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TenantKeyProvenanceAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "CITADEL0011";

    private const string KeyTypeName = "RoomKey";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Tenant key must derive from trusted context",
        messageFormat: "Cross-tenant risk: RoomKey tenant is built from untrusted input '{0}'; derive "
            + "the tenant from the authenticated session/connection, not from the wire envelope.",
        category: "Citadel.Tenancy",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A RoomKey's tenant component must originate from the authenticated context. "
            + "Trusted/untrusted identifier names are configured in repo-analyzers.json (invariants).");

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
        context.RegisterSyntaxNodeAction(ctx => AnalyzeCreation(ctx, config), SyntaxKind.ObjectCreationExpression);
    }

    private static void AnalyzeCreation(SyntaxNodeAnalysisContext context, InvariantConfig config)
    {
        var node = (ObjectCreationExpressionSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(node, context.CancellationToken).Symbol is not IMethodSymbol ctor)
        {
            return;
        }

        if (!string.Equals(ctor.ContainingType?.Name, KeyTypeName, StringComparison.Ordinal))
        {
            return;
        }

        var args = node.ArgumentList?.Arguments;
        if (args is null || args.Value.Count == 0)
        {
            return;
        }

        // The tenant component is the first positional argument of RoomKey(TenantId, RoomId).
        var tenantArg = args.Value[0].Expression;
        var root = RootIdentifier(tenantArg);
        if (root is not null && config.UntrustedInboundNames.Contains(root))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, tenantArg.GetLocation(), tenantArg.ToString()));
        }
    }

    /// <summary>The leftmost identifier of a member-access chain (e.g. <c>inbound</c> in <c>inbound.TenantId</c>).</summary>
    private static string? RootIdentifier(ExpressionSyntax expression)
    {
        var current = expression;
        while (true)
        {
            switch (current)
            {
                case IdentifierNameSyntax id:
                    return id.Identifier.Text;
                case MemberAccessExpressionSyntax member:
                    current = member.Expression;
                    break;
                case InvocationExpressionSyntax invocation:
                    current = invocation.Expression;
                    break;
                default:
                    return null;
            }
        }
    }
}
