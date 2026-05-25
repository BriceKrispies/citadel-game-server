using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Citadel.RepoAnalyzers;

/// <summary>
/// CITADEL0008 — protocol dispatch must be total. Any <c>switch</c> over the protocol message
/// discriminator (default <c>GameServer.Protocol.MessageType</c>) must have an explicit default
/// arm. Dispatch sites legitimately handle only a subset of the enum (inbound vs. outbound), so
/// full exhaustiveness is wrong; the real hazard is a newly added message type silently falling
/// through. A default arm (a <c>default:</c> section, or a <c>_</c> arm in a switch expression)
/// forces that case to be handled — typically a typed rejection.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MessageDispatchAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "CITADEL0008";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Protocol dispatch must have a default arm",
        messageFormat: "Dispatch over '{0}' has no default arm; a new message type would fall through "
            + "silently. Add a default case (typically a typed rejection).",
        category: "Citadel.Protocol",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Switches over the protocol message discriminator must have a default arm so an "
            + "added message type cannot be dropped silently. The enum is configured in repo-analyzers.json.");

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
        context.RegisterSyntaxNodeAction(ctx => AnalyzeSwitchStatement(ctx, config), SyntaxKind.SwitchStatement);
        context.RegisterSyntaxNodeAction(ctx => AnalyzeSwitchExpression(ctx, config), SyntaxKind.SwitchExpression);
    }

    private static void AnalyzeSwitchStatement(SyntaxNodeAnalysisContext context, InvariantConfig config)
    {
        var node = (SwitchStatementSyntax)context.Node;
        if (!GovernsDispatchEnum(context, node.Expression, config.DispatchEnum))
        {
            return;
        }

        foreach (var section in node.Sections)
        {
            foreach (var label in section.Labels)
            {
                if (label.IsKind(SyntaxKind.DefaultSwitchLabel))
                {
                    return;
                }
            }
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, node.GetLocation(), config.DispatchEnum));
    }

    private static void AnalyzeSwitchExpression(SyntaxNodeAnalysisContext context, InvariantConfig config)
    {
        var node = (SwitchExpressionSyntax)context.Node;
        if (!GovernsDispatchEnum(context, node.GoverningExpression, config.DispatchEnum))
        {
            return;
        }

        foreach (var arm in node.Arms)
        {
            // A discard pattern with no `when` clause is the total/default arm.
            if (arm.Pattern is DiscardPatternSyntax && arm.WhenClause is null)
            {
                return;
            }
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, node.GetLocation(), config.DispatchEnum));
    }

    private static bool GovernsDispatchEnum(SyntaxNodeAnalysisContext context, ExpressionSyntax expression, string enumFullName)
    {
        var type = context.SemanticModel.GetTypeInfo(expression, context.CancellationToken).Type;
        return type is not null && string.Equals(type.ToDisplayString(), enumFullName, StringComparison.Ordinal);
    }
}
