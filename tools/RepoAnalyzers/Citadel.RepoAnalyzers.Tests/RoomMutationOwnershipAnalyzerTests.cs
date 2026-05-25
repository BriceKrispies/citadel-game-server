using System.Threading.Tasks;
using Xunit;

namespace Citadel.RepoAnalyzers.Tests;

public sealed class RoomMutationOwnershipAnalyzerTests
{
    private const string Contract = @"
namespace GameServer.Simulation {
    public interface IGameSimulation { void Apply(int player, string command); }
}";

    private static Task<System.Collections.Immutable.ImmutableArray<Microsoft.CodeAnalysis.Diagnostic>> Run(string body) =>
        new InvariantScenario().Assembly("GameServer.Simulation").Source(Contract + "\n" + body)
            .RunAsync(new RoomMutationOwnershipAnalyzer());

    [Fact]
    public async Task Reports_Apply_Called_Outside_Owner()
    {
        var body = @"namespace S { using GameServer.Simulation; class Service {
            void Hack(IGameSimulation game) { game.Apply(1, ""x""); } } }";
        var d = Assert.Single(await Run(body));
        Assert.Equal(RoomMutationOwnershipAnalyzer.DiagnosticId, d.Id);
        Assert.Contains("Service", d.GetMessage());
    }

    [Fact]
    public async Task Allows_Apply_Called_From_Owner()
    {
        var body = @"namespace S { using GameServer.Simulation; class GameRoom {
            IGameSimulation _game;
            public void Tick() { _game.Apply(1, ""x""); }
            public bool Restore() { _game.Apply(2, ""y""); return true; } } }";
        Assert.Empty(await Run(body));
    }

    [Fact]
    public async Task Ignores_Unrelated_Apply_Method()
    {
        var body = @"namespace S { class Other { void Apply(int p, string c) { }
            void M() { new Other().Apply(1, ""x""); } } }";
        Assert.Empty(await Run(body));
    }
}
