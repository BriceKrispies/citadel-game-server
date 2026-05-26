using System.Threading.Tasks;
using Xunit;

namespace Citadel.RepoAnalyzers.Tests;

public sealed class TracePropagationAnalyzerTests
{
    private const string Sink = @"
namespace GameServer.Observability {
    using System.Collections.Generic;
    public interface ITelemetrySink {
        void Event(string name, IReadOnlyDictionary<string,string> fields);
    }
}";

    private static Task<System.Collections.Immutable.ImmutableArray<Microsoft.CodeAnalysis.Diagnostic>> Run(string body) =>
        new InvariantScenario().Assembly("GameServer.Transport").Source(Sink + "\n" + body)
            .RunAsync(new TracePropagationAnalyzer());

    private static string Build(string handlerSignature, string call) =>
        @"namespace T { using System.Collections.Generic; using GameServer.Observability;
            class H {
                ITelemetrySink _t;
                static IReadOnlyDictionary<string,string> Tags(params (string,string)[] t) => null;
                void Handle(" + handlerSignature + @") { " + call + @" }
            } }";

    [Fact]
    public async Task Reports_Inbound_Handler_Telemetry_Without_Trace()
    {
        var diagnostics = await Run(Build("string inbound", "_t.Event(\"command_rejected\", Tags((\"tenantId\", \"a\")));"));
        var d = Assert.Single(diagnostics);
        Assert.Equal(TracePropagationAnalyzer.DiagnosticId, d.Id);
    }

    [Fact]
    public async Task Allows_Inbound_Handler_Telemetry_With_Trace()
    {
        var diagnostics = await Run(Build("string inbound", "_t.Event(\"command_rejected\", Tags((\"traceId\", \"t\"), (\"tenantId\", \"a\")));"));
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Allows_Event_Built_From_Trusted_Correlation_Source()
    {
        // CorrelatedTags(connection, ...) carries the trace id in via the connection; the literal
        // keys at the call site are extra dimensions, not the whole tag set.
        var body = @"namespace T { using System.Collections.Generic; using GameServer.Observability;
            class Conn { }
            class H {
                ITelemetrySink _t;
                static IReadOnlyDictionary<string,string> CorrelatedTags(Conn c, params (string,string)[] t) => null;
                void Handle(string inbound, Conn connection) {
                    _t.Event(""identity_rejected"", CorrelatedTags(connection, (""declaredTenant"", ""a"")));
                } } }";
        Assert.Empty(await new InvariantScenario().Assembly("GameServer.Transport").Source(Sink + "\n" + body)
            .RunAsync(new TracePropagationAnalyzer()));
    }

    [Fact]
    public async Task Ignores_Opaque_Tag_Helper()
    {
        // Correlated-tags helper with no literal keys — conservative, not second-guessed.
        var diagnostics = await Run(Build("string inbound", "var f = Tags(); _t.Event(\"x\", f);"));
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Ignores_NonInbound_Handler()
    {
        var diagnostics = await Run(Build("int count", "_t.Event(\"flush\", Tags((\"n\", \"1\")));"));
        Assert.Empty(diagnostics);
    }
}
