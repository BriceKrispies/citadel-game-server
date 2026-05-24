using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Citadel.RepoAnalyzers.Tests;

public sealed class LayerDependencyAnalyzerTests
{
    // Mirrors the target ring model: kernel(0) = Protocol/Abstractions, layer 1 = Simulation/Tenancy,
    // layer 2 = Routing, layer 3 = Transport, with Host as a composition root.
    private const string Layering = """
    {
      "layering": {
        "universalRank": 0,
        "layers": {
          "GameServer.Protocol": 0,
          "GameServer.Abstractions": 0,
          "GameServer.Simulation": 1,
          "GameServer.Tenancy": 1,
          "GameServer.Routing": 2,
          "GameServer.Transport": 3
        },
        "compositionRoots": ["GameServer.Host"]
      }
    }
    """;

    private const string ThingSource = "namespace Inner { public class Thing { } }";
    private static string UsesThing(string ns) => $"namespace {ns} {{ class Consumer {{ Inner.Thing Field; }} }}";

    [Fact]
    public async Task UniversalKernel_IsAllowed()
    {
        var diagnostics = await new LayerScenario()
            .Assembly("GameServer.Transport")
            .ReferencedAssembly("GameServer.Protocol", ThingSource)
            .Source(UsesThing("Edge"))
            .Config(Layering)
            .RunAsync();

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task AdjacentInnerLayer_IsAllowed()
    {
        var diagnostics = await new LayerScenario()
            .Assembly("GameServer.Routing")
            .ReferencedAssembly("GameServer.Simulation", ThingSource)
            .Source(UsesThing("Composition"))
            .Config(Layering)
            .RunAsync();

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task SkippingARing_IsViolation()
    {
        var diagnostics = await new LayerScenario()
            .Assembly("GameServer.Transport")
            .ReferencedAssembly("GameServer.Simulation", ThingSource)
            .Source(UsesThing("Edge"))
            .Config(Layering)
            .RunAsync();

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(LayerDependencyAnalyzer.LayerViolationId, diagnostic.Id);
        var message = diagnostic.GetMessage();
        Assert.Contains("GameServer.Transport", message);
        Assert.Contains("GameServer.Simulation", message);
    }

    [Fact]
    public async Task ReachingOutward_IsViolation()
    {
        var diagnostics = await new LayerScenario()
            .Assembly("GameServer.Simulation")
            .ReferencedAssembly("GameServer.Routing", ThingSource)
            .Source(UsesThing("Core"))
            .Config(Layering)
            .RunAsync();

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(LayerDependencyAnalyzer.LayerViolationId, diagnostic.Id);
    }

    [Fact]
    public async Task SidewaysWithinSameLayer_IsViolation()
    {
        var diagnostics = await new LayerScenario()
            .Assembly("GameServer.Simulation")
            .ReferencedAssembly("GameServer.Tenancy", ThingSource)
            .Source(UsesThing("Core"))
            .Config(Layering)
            .RunAsync();

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(LayerDependencyAnalyzer.LayerViolationId, diagnostic.Id);
    }

    [Fact]
    public async Task IntraKernel_IsAllowed()
    {
        var diagnostics = await new LayerScenario()
            .Assembly("GameServer.Abstractions")
            .ReferencedAssembly("GameServer.Protocol", ThingSource)
            .Source(UsesThing("Ports"))
            .Config(Layering)
            .RunAsync();

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task CompositionRoot_IsExempt()
    {
        var diagnostics = await new LayerScenario()
            .Assembly("GameServer.Host")
            .ReferencedAssembly("GameServer.Simulation", ThingSource)
            .Source(UsesThing("Wiring"))
            .Config(Layering)
            .RunAsync();

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task NonGameServerTargets_AreIgnored()
    {
        var diagnostics = await new LayerScenario()
            .Assembly("GameServer.Simulation")
            .Source("namespace Core { class C { string S; int N; } }")
            .Config(Layering)
            .RunAsync();

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task UnmappedGameServerProject_IsViolation()
    {
        var diagnostics = await new LayerScenario()
            .Assembly("GameServer.NewThing")
            .Source("namespace NewThing { class C { } }")
            .Config(Layering)
            .RunAsync();

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(LayerDependencyAnalyzer.UnmappedProjectId, diagnostic.Id);
        Assert.Contains("GameServer.NewThing", diagnostic.GetMessage());
    }

    [Fact]
    public async Task Dormant_WhenNoLayeringConfigured()
    {
        // A skipping reference that WOULD violate, but the config has no layering section.
        var diagnostics = await new LayerScenario()
            .Assembly("GameServer.Transport")
            .ReferencedAssembly("GameServer.Simulation", ThingSource)
            .Source(UsesThing("Edge"))
            .Config("""{ "requiredTestSuffix": ".Tests.cs" }""")
            .RunAsync();

        Assert.Empty(diagnostics);
    }
}
