using System.Threading.Tasks;
using Xunit;

namespace Citadel.RepoAnalyzers.Tests;

public sealed class ProtocolPayloadImmutabilityAnalyzerTests
{
    private const string Marker = "namespace GameServer.Protocol { public interface IMessagePayload { } }";

    private static Task<System.Collections.Immutable.ImmutableArray<Microsoft.CodeAnalysis.Diagnostic>> Run(string source) =>
        new InvariantScenario().Assembly("GameServer.Protocol").Source(Marker + "\n" + source)
            .RunAsync(new ProtocolPayloadImmutabilityAnalyzer());

    [Fact]
    public async Task Reports_Mutable_Class_Payload()
    {
        var diagnostics = await Run("namespace P { public class Bad : GameServer.Protocol.IMessagePayload { public int X; } }");
        var d = Assert.Single(diagnostics);
        Assert.Equal(ProtocolPayloadImmutabilityAnalyzer.DiagnosticId, d.Id);
        Assert.Contains("Bad", d.GetMessage());
    }

    [Fact]
    public async Task Reports_NonSealed_Record_Payload()
    {
        var diagnostics = await Run("namespace P { public record Open(int X) : GameServer.Protocol.IMessagePayload; }");
        Assert.Single(diagnostics);
    }

    [Fact]
    public async Task Allows_Sealed_Record_Payload()
    {
        var diagnostics = await Run("namespace P { public sealed record Good(int X) : GameServer.Protocol.IMessagePayload; }");
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Allows_Record_Struct_Payload()
    {
        var diagnostics = await Run("namespace P { public record struct Val(int X) : GameServer.Protocol.IMessagePayload; }");
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Ignores_Type_Not_Implementing_Marker()
    {
        var diagnostics = await Run("namespace P { public class Plain { public int X; } }");
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Ignores_The_Marker_Interface_Itself()
    {
        // Re-declaring an interface that extends the marker must not be flagged.
        var diagnostics = await Run("namespace P { public interface IExtra : GameServer.Protocol.IMessagePayload { } }");
        Assert.Empty(diagnostics);
    }
}
