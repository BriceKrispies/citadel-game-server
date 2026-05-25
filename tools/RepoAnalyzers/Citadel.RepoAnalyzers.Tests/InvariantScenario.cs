using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Citadel.RepoAnalyzers.Tests;

/// <summary>
/// Builds a single self-contained compilation (with a chosen assembly name, full platform
/// references so <c>System.*</c> symbols resolve, and an optional <c>repo-analyzers.json</c>
/// AdditionalFile), runs one architectural-invariant analyzer against it, and returns that
/// analyzer's diagnostics. The test source declares any GameServer.* marker types it needs
/// (matching is by display name, not by real assembly), so no multi-project setup is required.
/// </summary>
internal sealed class InvariantScenario
{
    private static readonly ImmutableArray<MetadataReference> PlatformReferences = LoadPlatformReferences();

    private string _assemblyName = "GameServer.Simulation";
    private string _source = string.Empty;
    private string? _configJson;

    public InvariantScenario Assembly(string name)
    {
        _assemblyName = name;
        return this;
    }

    public InvariantScenario Source(string source)
    {
        _source = source;
        return this;
    }

    public InvariantScenario Config(string json)
    {
        _configJson = json;
        return this;
    }

    public async Task<ImmutableArray<Diagnostic>> RunAsync(DiagnosticAnalyzer analyzer)
    {
        var compilation = CSharpCompilation.Create(
            _assemblyName,
            new[] { CSharpSyntaxTree.ParseText(SourceText.From(_source), path: "/repo/src/Test.cs") },
            PlatformReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var additional = ImmutableArray<AdditionalText>.Empty;
        if (_configJson is not null)
        {
            additional = ImmutableArray.Create<AdditionalText>(
                new InMemoryAdditionalText("/repo/repo-analyzers.json", _configJson));
        }

        var withAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create(analyzer),
            new AnalyzerOptions(additional));

        var ids = analyzer.SupportedDiagnostics.Select(d => d.Id).ToImmutableHashSet();
        var diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync();
        return diagnostics.Where(d => ids.Contains(d.Id)).ToImmutableArray();
    }

    private static ImmutableArray<MetadataReference> LoadPlatformReferences()
    {
        var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (string.IsNullOrEmpty(tpa))
        {
            return ImmutableArray.Create<MetadataReference>(
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        }

        return tpa!
            .Split(Path.PathSeparator)
            .Where(p => p.Length > 0 && File.Exists(p))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToImmutableArray();
    }

    private sealed class InMemoryAdditionalText : AdditionalText
    {
        private readonly SourceText _text;

        public InMemoryAdditionalText(string path, string content)
        {
            Path = path;
            _text = SourceText.From(content);
        }

        public override string Path { get; }

        public override SourceText? GetText(CancellationToken cancellationToken = default) => _text;
    }
}
