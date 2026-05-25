using System.Threading.Tasks;
using Xunit;

namespace Citadel.RepoAnalyzers.Tests;

public sealed class BoundedQueueAnalyzerTests
{
    private static Task<System.Collections.Immutable.ImmutableArray<Microsoft.CodeAnalysis.Diagnostic>> Run(
        string source, string assembly = "GameServer.Transport") =>
        new InvariantScenario().Assembly(assembly).Source(source).RunAsync(new BoundedQueueAnalyzer());

    private const string UsesChannel =
        "using System.Threading.Channels; namespace T { public class {0} {{ Channel<int> _c = Channel.CreateUnbounded<int>(); }} }";

    [Fact]
    public async Task Reports_Unbounded_Channel_In_Transport()
    {
        var diagnostics = await Run(string.Format(UsesChannel, "HotPath"));
        var d = Assert.Single(diagnostics);
        Assert.Equal(BoundedQueueAnalyzer.DiagnosticId, d.Id);
    }

    [Fact]
    public async Task Allows_Bounded_Channel_In_Transport()
    {
        var src = "using System.Threading.Channels; namespace T { public class Ok { Channel<int> _c = Channel.CreateBounded<int>(256); } }";
        var diagnostics = await Run(src);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Allows_Unbounded_Channel_In_Allowlisted_Type()
    {
        // InMemoryBidirectionalTransport is the seeded allowlist entry (in-process test transport).
        var diagnostics = await Run(string.Format(UsesChannel, "InMemoryBidirectionalTransport"));
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Ignores_Unbounded_Channel_Outside_Bounded_Assemblies()
    {
        var diagnostics = await Run(string.Format(UsesChannel, "Harness"), "GameServer.LoadHarness");
        Assert.Empty(diagnostics);
    }
}
