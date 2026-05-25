using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Citadel.RepoAnalyzers;

/// <summary>
/// CITADEL0009 — only the room owner mutates room state. The authoritative mutation entry point
/// (<c>IGameSimulation.Apply</c>) may be invoked only from within the room owner type (default
/// <c>GameRoom</c>): commands are validated then queued, and applied on the tick/recovery paths
/// the room itself owns. A service or transport reaching in to call <c>Apply</c> directly would
/// bypass sequencing, validation, and the actor boundary.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RoomMutationOwnershipAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "CITADEL0009";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Room state may be mutated only by the room owner",
        messageFormat: "Room-ownership violation: '{0}' invokes the authoritative mutation '{1}.{2}' "
            + "outside the room owner type '{3}'; route mutations through the room's command queue.",
        category: "Citadel.Architecture",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The simulation mutation method may be called only from within the room owner type, "
            + "preserving the actor boundary. Configured in repo-analyzers.json (invariants).");

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

        if (!string.Equals(method.Name, config.MutationMethod, StringComparison.Ordinal))
        {
            return;
        }

        // The call must target the simulation contract: either the interface method directly,
        // or an implementation of it. `_game.Apply(...)` binds to the interface method.
        if (!TargetsSimulationContract(method, config.SimulationInterface))
        {
            return;
        }

        var owner = EnclosingTypeName(node);
        if (string.Equals(owner, config.MutationOwnerType, StringComparison.Ordinal))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            Rule,
            node.GetLocation(),
            owner ?? "<unknown>",
            method.ContainingType?.Name ?? "IGameSimulation",
            method.Name,
            config.MutationOwnerType));
    }

    private static bool TargetsSimulationContract(IMethodSymbol method, string interfaceFullName)
    {
        var containing = method.ContainingType;
        if (containing is null)
        {
            return false;
        }

        if (string.Equals(containing.ToDisplayString(), interfaceFullName, StringComparison.Ordinal))
        {
            return true;
        }

        // An implementing type's own Apply: check it implements the simulation interface.
        return containing.AllInterfaces.Any(i =>
            string.Equals(i.ToDisplayString(), interfaceFullName, StringComparison.Ordinal));
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
