using System.Threading.Tasks;
using Xunit;

namespace Citadel.RepoAnalyzers.Tests;

public sealed class TelemetryContextAnalyzerTests
{
    private const string Sink = @"
namespace GameServer.Observability {
    using System.Collections.Generic;
    public interface ITelemetrySink {
        void Increment(string metric, IReadOnlyDictionary<string,string> tags);
        void Event(string name, IReadOnlyDictionary<string,string> fields);
    }
}";

    // Schema requires ConnectionsOpened to carry tenantId + connectionId.
    private const string SchemaConfig =
        "{ \"invariants\": { \"requiredTagsByMetric\": [ \"ConnectionsOpened=tenantId,connectionId\" ] } }";

    private static Task<System.Collections.Immutable.ImmutableArray<Microsoft.CodeAnalysis.Diagnostic>> Run(
        string body, string? config = SchemaConfig)
    {
        var scenario = new InvariantScenario().Assembly("GameServer.Transport").Source(Sink + "\n" + body);
        if (config is not null)
        {
            scenario = scenario.Config(config);
        }

        return scenario.RunAsync(new TelemetryContextAnalyzer());
    }

    private const string Helper = @"namespace T { using System.Collections.Generic; using GameServer.Observability;
        class H {
            ITelemetrySink _t;
            static IReadOnlyDictionary<string,string> Tags(params (string,string)[] t) => null;
            const string ConnectionsOpened = ""connections_opened"";
            void M() { {{BODY}} }
        } }";

    private static string With(string call) => Helper.Replace("{{BODY}}", call);

    [Fact]
    public async Task Reports_Missing_Required_Tag()
    {
        // Has tenantId but not connectionId.
        var diagnostics = await Run(With("_t.Increment(ConnectionsOpened, Tags((\"tenantId\", \"a\")));"));
        var d = Assert.Single(diagnostics);
        Assert.Equal(TelemetryContextAnalyzer.DiagnosticId, d.Id);
        Assert.Contains("connectionId", d.GetMessage());
    }

    [Fact]
    public async Task Allows_All_Required_Tags_Present()
    {
        var diagnostics = await Run(With("_t.Increment(ConnectionsOpened, Tags((\"tenantId\", \"a\"), (\"connectionId\", \"c\")));"));
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Ignores_Opaque_Tag_Helper_With_No_Literal_Keys()
    {
        // Cannot prove a violation when no literal keys are present — conservative, stays silent.
        var diagnostics = await Run(With("var tags = Tags(); _t.Increment(ConnectionsOpened, tags);"));
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Ignores_Metric_Not_In_Schema()
    {
        var diagnostics = await Run(With("_t.Increment(\"something_else\", Tags((\"x\", \"y\")));"));
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Dormant_When_No_Schema_Configured()
    {
        var diagnostics = await Run(With("_t.Increment(ConnectionsOpened, Tags((\"tenantId\", \"a\")));"), config: null);
        Assert.Empty(diagnostics);
    }
}
