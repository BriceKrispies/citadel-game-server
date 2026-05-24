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
/// Builds a synthetic compilation with chosen file paths + additional files, runs
/// <see cref="CoLocatedTestAnalyzer"/> against it, and returns the rule's diagnostics.
/// Lets the analyzer tests assert behavior without touching the real file system.
/// </summary>
internal sealed class AnalyzerScenario
{
    private readonly List<(string Path, string Content)> _production = new();
    private readonly List<string> _existingTestFiles = new();
    private string? _configJson;

    public AnalyzerScenario Production(string path, string content = "")
    {
        _production.Add((path, content));
        return this;
    }

    /// <summary>Registers a test file the build "knows about" (passed to the analyzer as an additional file).</summary>
    public AnalyzerScenario ExistingTestFile(string path)
    {
        _existingTestFiles.Add(path);
        return this;
    }

    public AnalyzerScenario Config(string json)
    {
        _configJson = json;
        return this;
    }

    public async Task<ImmutableArray<Diagnostic>> RunAsync()
    {
        var trees = _production
            .Select(p => CSharpSyntaxTree.ParseText(SourceText.From(p.Content), path: p.Path))
            .ToImmutableArray<SyntaxTree>();

        var additional = new List<AdditionalText>();
        foreach (var testFile in _existingTestFiles)
        {
            additional.Add(new InMemoryAdditionalText(testFile, string.Empty));
        }

        if (_configJson is not null)
        {
            additional.Add(new InMemoryAdditionalText("/repo/repo-analyzers.json", _configJson));
        }

        var compilation = CSharpCompilation.Create(
            "Scenario",
            trees,
            references: Array.Empty<MetadataReference>());

        var options = new AnalyzerOptions(additional.ToImmutableArray());
        var withAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new CoLocatedTestAnalyzer()),
            options);

        var diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync();
        return diagnostics
            .Where(d => d.Id == CoLocatedTestAnalyzer.DiagnosticId)
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
