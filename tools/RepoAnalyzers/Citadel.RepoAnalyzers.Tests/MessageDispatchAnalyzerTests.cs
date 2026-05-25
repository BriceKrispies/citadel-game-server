using System.Threading.Tasks;
using Xunit;

namespace Citadel.RepoAnalyzers.Tests;

public sealed class MessageDispatchAnalyzerTests
{
    private const string Enum =
        "namespace GameServer.Protocol { public enum MessageType { ClientHello, ClientCommand, ServerError } }";

    private static Task<System.Collections.Immutable.ImmutableArray<Microsoft.CodeAnalysis.Diagnostic>> Run(string body) =>
        new InvariantScenario().Assembly("GameServer.Transport").Source(Enum + "\n" + body)
            .RunAsync(new MessageDispatchAnalyzer());

    [Fact]
    public async Task Reports_SwitchStatement_Without_Default()
    {
        var body = @"namespace T { using GameServer.Protocol; class D {
            void M(MessageType t) { switch (t) {
                case MessageType.ClientHello: break;
                case MessageType.ClientCommand: break; } } } }";
        var d = Assert.Single(await Run(body));
        Assert.Equal(MessageDispatchAnalyzer.DiagnosticId, d.Id);
    }

    [Fact]
    public async Task Allows_SwitchStatement_With_Default()
    {
        var body = @"namespace T { using GameServer.Protocol; class D {
            void M(MessageType t) { switch (t) {
                case MessageType.ClientHello: break;
                default: break; } } } }";
        Assert.Empty(await Run(body));
    }

    [Fact]
    public async Task Reports_SwitchExpression_Without_Discard()
    {
        var body = @"namespace T { using GameServer.Protocol; class D {
            int M(MessageType t) => t switch {
                MessageType.ClientHello => 1,
                MessageType.ClientCommand => 2,
                MessageType.ServerError => 3 }; } }";
        Assert.Single(await Run(body));
    }

    [Fact]
    public async Task Allows_SwitchExpression_With_Discard()
    {
        var body = @"namespace T { using GameServer.Protocol; class D {
            int M(MessageType t) => t switch {
                MessageType.ClientHello => 1,
                _ => 0 }; } }";
        Assert.Empty(await Run(body));
    }

    [Fact]
    public async Task Ignores_Switch_Over_Other_Enum()
    {
        var body = @"namespace T { enum Color { Red, Green } class D {
            void M(Color c) { switch (c) { case Color.Red: break; } } } }";
        Assert.Empty(await Run(body));
    }
}
