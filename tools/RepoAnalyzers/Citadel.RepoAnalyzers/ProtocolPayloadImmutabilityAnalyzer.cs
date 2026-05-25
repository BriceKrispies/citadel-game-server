using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Citadel.RepoAnalyzers;

/// <summary>
/// CITADEL0005 — every protocol payload must be an immutable, closed contract. A type that
/// implements the payload marker (default <c>GameServer.Protocol.IMessagePayload</c>) must be a
/// <c>sealed record</c> (or a <c>record struct</c>, which is implicitly sealed and immutable by
/// position). A mutable <c>class</c> payload would let the wire contract drift and break
/// codec/version assumptions.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ProtocolPayloadImmutabilityAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "CITADEL0005";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Protocol payload must be a sealed record",
        messageFormat: "Protocol payload '{0}' implements the payload marker but is not a sealed record; "
            + "protocol payloads must be immutable, closed contracts.",
        category: "Citadel.Protocol",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Types implementing the protocol payload marker must be sealed records so the wire "
            + "contract is immutable and non-extensible. The marker is configured in repo-analyzers.json.");

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
        context.RegisterSymbolAction(ctx => AnalyzeType(ctx, config.PayloadMarker), SymbolKind.NamedType);
    }

    private static void AnalyzeType(SymbolAnalysisContext context, string markerFullName)
    {
        var type = (INamedTypeSymbol)context.Symbol;

        // Only concrete payload implementations are policed — not the marker interface itself,
        // nor other interfaces extending it.
        if (type.TypeKind == TypeKind.Interface)
        {
            return;
        }

        var implementsMarker = type.AllInterfaces.Any(i =>
            string.Equals(i.ToDisplayString(), markerFullName, StringComparison.Ordinal));
        if (!implementsMarker)
        {
            return;
        }

        // A record struct is implicitly sealed and immutable-by-position; a record class must be
        // explicitly sealed so no subclass can add mutable, contract-breaking state.
        if (type.IsRecord && (type.IsSealed || type.IsValueType))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            Rule, type.Locations.FirstOrDefault() ?? Location.None, type.Name));
    }
}
