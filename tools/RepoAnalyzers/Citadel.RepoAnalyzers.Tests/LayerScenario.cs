using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Citadel.RepoAnalyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Citadel.RepoAnalyzers.Tests;

/// <summary>
/// Builds a consuming compilation (with a chosen assembly name) that references one or more
/// "layer" compilations (each with its own assembly name), runs
/// <see cref="LayerDependencyAnalyzer"/>, and returns its diagnostics. This exercises the
/// cross-assembly rule the way a real multi-project build does, without touching disk.
/// </summary>
internal sealed class LayerScenario
{
    private static readonly ImmutableArray<MetadataReference> BclReferences =
        ImmutableArray.Create<MetadataReference>(
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location));

    private string _assemblyName = "GameServer.Consumer";
    private string _source = string.Empty;
    private string? _configJson;
    private readonly List<(string Name, string Source)> _referenced = new();

    public LayerScenario Assembly(string name)
    {
        _assemblyName = name;
        return this;
    }

    public LayerScenario Source(string source)
    {
        _source = source;
        return this;
    }

    public LayerScenario Config(string json)
    {
        _configJson = json;
        return this;
    }

    /// <summary>Registers an assembly the consumer references, named so it maps to a layer.</summary>
    public LayerScenario ReferencedAssembly(string name, string source)
    {
        _referenced.Add((name, source));
        return this;
    }

    public async Task<ImmutableArray<Diagnostic>> RunAsync()
    {
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);

        var references = new List<MetadataReference>(BclReferences);
        foreach (var (name, source) in _referenced)
        {
            var referenced = CSharpCompilation.Create(
                name,
                new[] { CSharpSyntaxTree.ParseText(SourceText.From(source)) },
                BclReferences,
                options);
            references.Add(referenced.ToMetadataReference());
        }

        var consumer = CSharpCompilation.Create(
            _assemblyName,
            new[] { CSharpSyntaxTree.ParseText(SourceText.From(_source)) },
            references,
            options);

        var additional = ImmutableArray<AdditionalText>.Empty;
        if (_configJson is not null)
        {
            additional = ImmutableArray.Create<AdditionalText>(
                new InMemoryAdditionalText("/repo/repo-analyzers.json", _configJson));
        }

        var withAnalyzers = consumer.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new LayerDependencyAnalyzer()),
            new AnalyzerOptions(additional));

        var diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync();
        return diagnostics
            .Where(d => d.Id is LayerDependencyAnalyzer.LayerViolationId or LayerDependencyAnalyzer.UnmappedProjectId)
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
