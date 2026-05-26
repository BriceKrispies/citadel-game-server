using System.Threading.Tasks;
using Xunit;

namespace Citadel.RepoAnalyzers.Tests;

public sealed class AuthenticatedPlayerAnalyzerTests
{
    private static Task<System.Collections.Immutable.ImmutableArray<Microsoft.CodeAnalysis.Diagnostic>> Run(string body) =>
        new InvariantScenario().Assembly("GameServer.Transport").Source(body)
            .RunAsync(new AuthenticatedPlayerAnalyzer());

    private static string Build(string call) =>
        @"namespace T {
            class Envelope { public int PlayerId; }
            class Connection { public int Player; }
            class Room { public void TryEnqueue(int player, string cmd) { } }
            class H {
                Envelope inbound = new Envelope();
                Connection connection = new Connection();
                Room room = new Room();
                void M() { " + call + @" }
            } }";

    [Fact]
    public async Task Reports_Inbound_PlayerId_Into_Mutation()
    {
        var d = Assert.Single(await Run(Build("room.TryEnqueue(inbound.PlayerId, \"x\");")));
        Assert.Equal(AuthenticatedPlayerAnalyzer.DiagnosticId, d.Id);
        Assert.Contains("inbound.PlayerId", d.GetMessage());
    }

    [Fact]
    public async Task Allows_Connection_Player_Into_Mutation()
    {
        Assert.Empty(await Run(Build("room.TryEnqueue(connection.Player, \"x\");")));
    }

    [Fact]
    public async Task Ignores_Inbound_PlayerId_Outside_Mutation()
    {
        // Passing inbound.PlayerId to a non-mutation method (e.g. logging) is not policed here.
        var body = @"namespace T {
            class Envelope { public int PlayerId; }
            class H { Envelope inbound = new Envelope(); void Log(int p) { } void M() { Log(inbound.PlayerId); } } }";
        Assert.Empty(await Run(body));
    }
}
