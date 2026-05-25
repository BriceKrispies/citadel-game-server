using System.Linq;
using GameServer.Protocol;
using GameServer.Routing;
using Xunit;

namespace GameServer.Cluster.Redis;

/// <summary>
/// Deterministic (no-Docker) pin for the Redis key encoding — the tenant-isolation fix. The Redis
/// owner key is derived from (tenant, room); the encoding MUST be injective or two distinct tenants
/// could collide on one owner key (cross-tenant room hijack). Runs in the fast loop.
/// </summary>
public sealed class RedisClusterKeysTests
{
    [Fact]
    public void Member_IsInjective_AcrossSlashBearingTenantAndRoomIds()
    {
        // The classic collision: tenant "a/b" + room "c" vs tenant "a" + room "b/c" — both would be
        // "a/b/c" under naive concatenation. Escaping the segments keeps them distinct.
        var left = RedisClusterKeys.Member(new RoomKey(new TenantId("a/b"), new RoomId("c")));
        var right = RedisClusterKeys.Member(new RoomKey(new TenantId("a"), new RoomId("b/c")));

        Assert.NotEqual(left, right);
    }

    [Fact]
    public void Member_EscapesHashTagAndDelimiterChars()
    {
        var member = RedisClusterKeys.Member(new RoomKey(new TenantId("t{x}:1"), new RoomId("r/2")));

        Assert.DoesNotContain('{', member);                 // no Redis cluster hash-tag injection
        Assert.DoesNotContain('}', member);
        Assert.Equal(1, member.Count(c => c == '/'));        // the only '/' is the tenant|room separator
    }
}
