using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Citadel.RepoAnalyzers;

/// <summary>
/// CITADEL0007 — room identity must travel with tenant identity. In a tenant-scoped assembly
/// (default Transport / Routing) a method or constructor that takes a bare <c>RoomId</c> must also
/// take a tenant-bearing parameter (<c>TenantId</c>, <c>TenantContext</c>, <c>Session</c>) or the
/// combined <c>RoomKey</c>. A <c>RoomId</c> alone is ambiguous across tenants and is how
/// cross-tenant lookups slip in. Known boundary exceptions (e.g. the IPC room proxy, tracked as a
/// manifest gap) are allowlisted in config.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TenantContextAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "CITADEL0007";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Room identity must carry tenant context",
        messageFormat: "Tenant-isolation risk: '{0}' takes a RoomId without any tenant context; pass a "
            + "RoomKey or a tenant-bearing parameter so room lookups cannot cross tenants.",
        category: "Citadel.Tenancy",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "In tenant-scoped assemblies a RoomId parameter must be accompanied by tenant "
            + "context. Tenant-bearing types and exemptions are configured in repo-analyzers.json.");

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
        if (!config.TenantScopedAssemblies.Contains(assembly))
        {
            return;
        }

        context.RegisterSymbolAction(ctx => AnalyzeMethod(ctx, config), SymbolKind.Method);
    }

    private static void AnalyzeMethod(SymbolAnalysisContext context, InvariantConfig config)
    {
        var method = (IMethodSymbol)context.Symbol;

        // Only ordinary methods and constructors define call surfaces that build room lookups.
        // Property/event accessors, operators, lambdas, etc. are not policed.
        if (method.MethodKind != MethodKind.Ordinary && method.MethodKind != MethodKind.Constructor)
        {
            return;
        }

        // Skip compiler-synthesized members (e.g. a record's generated Equals(RoomId),
        // Deconstruct, or copy-constructor) — those are not authored call surfaces.
        if (method.IsImplicitlyDeclared)
        {
            return;
        }

        var declaringType = method.ContainingType?.Name;
        if (declaringType is not null && config.TenantContextExemptTypes.Contains(declaringType))
        {
            return;
        }

        var hasRoomId = false;
        var hasTenant = false;
        foreach (var parameter in method.Parameters)
        {
            var typeName = parameter.Type.Name;
            if (string.Equals(typeName, config.RoomIdTypeName, StringComparison.Ordinal))
            {
                hasRoomId = true;
            }

            if (config.TenantParamTypes.Contains(typeName))
            {
                hasTenant = true;
            }
        }

        if (hasRoomId && !hasTenant)
        {
            var name = method.MethodKind == MethodKind.Constructor
                ? (declaringType ?? "<ctor>") + " constructor"
                : (declaringType is null ? method.Name : declaringType + "." + method.Name);
            context.ReportDiagnostic(Diagnostic.Create(
                Rule, method.Locations.Length > 0 ? method.Locations[0] : Location.None, name));
        }
    }
}
