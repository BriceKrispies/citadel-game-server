using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Citadel.RepoAnalyzers;

/// <summary>
/// CITADEL0004 — the authoritative simulation must be deterministic. Inside a simulation
/// assembly (default <c>GameServer.Simulation</c>) no code may read wall-clock time or ambient
/// randomness: those make a room non-replayable and untestable. Time is injected via
/// <c>ISimulationClock</c> (tick count) and randomness via a seeded <c>IRandomSource</c>; the
/// host owns real time.
///
/// Banned: <c>DateTime.Now/UtcNow/Today</c>, <c>DateTimeOffset.Now/UtcNow</c>, <c>Stopwatch</c>,
/// <c>Random.Shared</c>, unseeded <c>new Random()</c>, <c>Guid.NewGuid</c>,
/// <c>Environment.TickCount(64)</c>, <c>Thread.Sleep</c>, <c>Task.Delay</c>. A seeded
/// <c>new Random(seed)</c> is allowed (that is how <c>SeededRandomSource</c> is built).
/// Complements the layer rule (CITADEL0002), which only bans cross-ring references.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SimulationPurityAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "CITADEL0004";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Simulation must not use wall-clock time or ambient randomness",
        messageFormat: "Simulation purity violation: '{0}' is non-deterministic and is banned in the "
            + "simulation kernel; inject time via ISimulationClock and randomness via a seeded IRandomSource.",
        category: "Citadel.Architecture",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Code in a simulation assembly must be deterministic so rooms replay and test "
            + "identically. Banned APIs are configured in repo-analyzers.json (invariants).");

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
        if (!config.SimulationAssemblies.Contains(assembly))
        {
            // The rule is scoped to the kernel; other assemblies legitimately use wall-clock time.
            return;
        }

        context.RegisterSyntaxNodeAction(
            ctx => AnalyzeMemberAccess(ctx, config),
            SyntaxKind.SimpleMemberAccessExpression);

        context.RegisterSyntaxNodeAction(
            ctx => AnalyzeObjectCreation(ctx, config),
            SyntaxKind.ObjectCreationExpression);
    }

    private static void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context, InvariantConfig config)
    {
        var node = (MemberAccessExpressionSyntax)context.Node;
        var symbol = context.SemanticModel.GetSymbolInfo(node, context.CancellationToken).Symbol;
        var containing = symbol?.ContainingType;
        if (containing is null)
        {
            return;
        }

        var typeName = containing.ToDisplayString();
        if (config.SimulationBannedTypes.Contains(typeName))
        {
            Report(context, node, $"{containing.Name}.{symbol!.Name}");
            return;
        }

        var member = typeName + "." + symbol!.Name;
        if (config.SimulationBannedMembers.Contains(member))
        {
            Report(context, node, member);
        }
    }

    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context, InvariantConfig config)
    {
        var node = (ObjectCreationExpressionSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(node, context.CancellationToken).Symbol is not IMethodSymbol ctor)
        {
            return;
        }

        var typeName = ctor.ContainingType.ToDisplayString();
        if (config.SimulationBannedTypes.Contains(typeName))
        {
            Report(context, node, ctor.ContainingType.Name);
            return;
        }

        // Unseeded `new Random()` draws from a time-based seed — non-deterministic. A seeded
        // `new Random(seed)` is the deterministic construction used by SeededRandomSource.
        if (typeName == "System.Random" && (node.ArgumentList is null || node.ArgumentList.Arguments.Count == 0))
        {
            Report(context, node, "new Random() (unseeded)");
        }
    }

    private static void Report(SyntaxNodeAnalysisContext context, SyntaxNode node, string what) =>
        context.ReportDiagnostic(Diagnostic.Create(Rule, node.GetLocation(), what));
}
