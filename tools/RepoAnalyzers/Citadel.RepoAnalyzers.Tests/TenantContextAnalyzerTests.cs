using System.Threading.Tasks;
using Xunit;

namespace Citadel.RepoAnalyzers.Tests;

public sealed class TenantContextAnalyzerTests
{
    // Minimal identity types matching the names the rule keys off (by simple type name).
    private const string Types = @"
namespace GameServer.Protocol { public readonly record struct RoomId(string Value); }
namespace GameServer.Tenancy {
    public readonly record struct TenantId(string Value);
    public sealed record TenantContext(TenantId TenantId);
    public readonly record struct RoomKey(TenantId TenantId, GameServer.Protocol.RoomId RoomId);
}";

    private static Task<System.Collections.Immutable.ImmutableArray<Microsoft.CodeAnalysis.Diagnostic>> Run(
        string body, string assembly = "GameServer.Routing") =>
        new InvariantScenario().Assembly(assembly).Source(Types + "\n" + body).RunAsync(new TenantContextAnalyzer());

    [Fact]
    public async Task Reports_Method_With_Bare_RoomId()
    {
        var body = @"namespace R { using GameServer.Protocol; class Router {
            public void GetRoom(RoomId roomId) { } } }";
        var d = Assert.Single(await Run(body));
        Assert.Equal(TenantContextAnalyzer.DiagnosticId, d.Id);
        Assert.Contains("Router.GetRoom", d.GetMessage());
    }

    [Fact]
    public async Task Allows_Method_With_TenantContext_And_RoomId()
    {
        var body = @"namespace R { using GameServer.Protocol; using GameServer.Tenancy; class Router {
            public void GetRoom(TenantContext tenant, RoomId roomId) { } } }";
        Assert.Empty(await Run(body));
    }

    [Fact]
    public async Task Allows_Method_With_RoomKey()
    {
        var body = @"namespace R { using GameServer.Tenancy; class Router {
            public void GetRoom(RoomKey key) { } } }";
        Assert.Empty(await Run(body));
    }

    [Fact]
    public async Task Allows_Exempt_Type()
    {
        // RemoteGameRoom is the seeded exemption (IPC boundary keyed by RoomId — tracked manifest gap).
        var body = @"namespace R { using GameServer.Protocol; class RemoteGameRoom {
            public RemoteGameRoom(RoomId id) { } } }";
        Assert.Empty(await Run(body));
    }

    [Fact]
    public async Task Ignores_RoomId_Method_Outside_TenantScoped_Assembly()
    {
        var body = @"namespace R { using GameServer.Protocol; class Catalog {
            public void GetRoom(RoomId roomId) { } } }";
        Assert.Empty(await Run(body, "GameServer.ControlPlane"));
    }

    [Fact]
    public async Task Ignores_RoomId_Typed_Property()
    {
        // A property getter is not an ordinary method or constructor, so it is not policed.
        var body = @"namespace R { using GameServer.Protocol; class Holder { public RoomId Id { get; } } }";
        Assert.Empty(await Run(body, "GameServer.Transport"));
    }
}
