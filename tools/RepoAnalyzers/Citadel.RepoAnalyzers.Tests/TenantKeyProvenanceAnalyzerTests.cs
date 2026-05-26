using System.Threading.Tasks;
using Xunit;

namespace Citadel.RepoAnalyzers.Tests;

public sealed class TenantKeyProvenanceAnalyzerTests
{
    private const string Types = @"
namespace GameServer.Protocol { public readonly record struct RoomId(string Value); }
namespace GameServer.Tenancy {
    public readonly record struct TenantId(string Value);
    public readonly record struct RoomKey(TenantId TenantId, GameServer.Protocol.RoomId RoomId);
}";

    private static Task<System.Collections.Immutable.ImmutableArray<Microsoft.CodeAnalysis.Diagnostic>> Run(string body) =>
        new InvariantScenario().Assembly("GameServer.Transport").Source(Types + "\n" + body)
            .RunAsync(new TenantKeyProvenanceAnalyzer());

    private static string Build(string keyExpr) =>
        @"namespace T { using GameServer.Protocol; using GameServer.Tenancy;
            class Inbound { public TenantId TenantId; public RoomId RoomId; }
            class Session { public TenantId Tenant; }
            class H {
                Inbound inbound = new Inbound();
                Session session = new Session();
                RoomId room = new RoomId(""r"");
                RoomKey Make() => " + keyExpr + @";
            } }";

    [Fact]
    public async Task Reports_Key_Built_From_Inbound_Tenant()
    {
        var d = Assert.Single(await Run(Build("new RoomKey(inbound.TenantId, room)")));
        Assert.Equal(TenantKeyProvenanceAnalyzer.DiagnosticId, d.Id);
        Assert.Contains("inbound.TenantId", d.GetMessage());
    }

    [Fact]
    public async Task Allows_Key_Built_From_Session()
    {
        Assert.Empty(await Run(Build("new RoomKey(session.Tenant, room)")));
    }

    [Fact]
    public async Task Allows_Key_Built_From_Local_TenantId()
    {
        var body = @"namespace T { using GameServer.Protocol; using GameServer.Tenancy;
            class H { RoomKey Make(TenantId tenant, RoomId room) => new RoomKey(tenant, room); } }";
        Assert.Empty(await Run(body));
    }
}
