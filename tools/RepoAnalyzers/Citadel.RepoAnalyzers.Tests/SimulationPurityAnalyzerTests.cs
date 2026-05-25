using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Citadel.RepoAnalyzers.Tests;

public sealed class SimulationPurityAnalyzerTests
{
    private static Task<System.Collections.Immutable.ImmutableArray<Microsoft.CodeAnalysis.Diagnostic>> Run(
        string source, string assembly = "GameServer.Simulation") =>
        new InvariantScenario().Assembly(assembly).Source(source).RunAsync(new SimulationPurityAnalyzer());

    [Fact]
    public async Task Reports_DateTime_UtcNow_In_Simulation()
    {
        var diagnostics = await Run("class Sim { System.DateTime When() => System.DateTime.UtcNow; }");
        var d = Assert.Single(diagnostics);
        Assert.Equal(SimulationPurityAnalyzer.DiagnosticId, d.Id);
        Assert.Contains("System.DateTime.UtcNow", d.GetMessage());
    }

    [Fact]
    public async Task Reports_Stopwatch_Usage_In_Simulation()
    {
        var diagnostics = await Run("class Sim { void M() { var s = new System.Diagnostics.Stopwatch(); s.Start(); } }");
        Assert.NotEmpty(diagnostics);
        Assert.All(diagnostics, d => Assert.Equal(SimulationPurityAnalyzer.DiagnosticId, d.Id));
    }

    [Fact]
    public async Task Reports_Random_Shared_In_Simulation()
    {
        var diagnostics = await Run("class Sim { int R() => System.Random.Shared.Next(); }");
        Assert.NotEmpty(diagnostics);
    }

    [Fact]
    public async Task Reports_Unseeded_New_Random_In_Simulation()
    {
        var diagnostics = await Run("class Sim { System.Random R() => new System.Random(); }");
        var d = Assert.Single(diagnostics);
        Assert.Contains("unseeded", d.GetMessage());
    }

    [Fact]
    public async Task Reports_Guid_NewGuid_And_Thread_Sleep()
    {
        var diagnostics = await Run(
            "class Sim { void M() { var g = System.Guid.NewGuid(); System.Threading.Thread.Sleep(1); } }");
        Assert.Equal(2, diagnostics.Length);
    }

    [Fact]
    public async Task Allows_Seeded_New_Random_In_Simulation()
    {
        // This is exactly how SeededRandomSource is constructed — deterministic, allowed.
        var diagnostics = await Run("class Sim { System.Random R() => new System.Random(1); }");
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Ignores_Same_Code_Outside_Simulation_Assembly()
    {
        // Wall-clock time is legitimate in the host; the rule is scoped to the kernel assembly.
        var diagnostics = await Run("class Host { System.DateTime When() => System.DateTime.UtcNow; }", "GameServer.Host");
        Assert.Empty(diagnostics);
    }
}
