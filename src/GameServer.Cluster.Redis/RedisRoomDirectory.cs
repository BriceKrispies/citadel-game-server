using GameServer.Routing;
using StackExchange.Redis;

namespace GameServer.Cluster.Redis;

/// <summary>Tuning for the Redis-backed cluster directory/placement.</summary>
/// <param name="KeyPrefix">Namespace for all keys, so multiple environments can share a server.</param>
/// <param name="LeaseMs">
/// How long a claim lives without renewal. A live node renews its rooms well inside this window via the
/// host's lease-renewal worker (which re-claims each served room — see <c>RoomLeaseRenewalService</c>);
/// if a node dies, its claims expire after this window and the rooms become reclaimable, dropping out of
/// capacity counts automatically (counts are by un-expired lease, see <see cref="RedisRoomDirectory.OwnedCount"/>).
/// </param>
public sealed record RedisClusterOptions(string KeyPrefix = "citadel", int LeaseMs = 30_000);

/// <summary>Builds the Redis keys/members shared by the directory and placement adapters.</summary>
public static class RedisClusterKeys
{
    // Segments are percent-escaped so the encoding is INJECTIVE: '/' (the separator), ':' (the key
    // delimiter) and '{' '}' (Redis cluster hash-tag markers) cannot appear raw inside a segment.
    // Without this, tenant "a/b"+room "c" and tenant "a"+room "b/c" would collide on one owner key —
    // a cross-tenant isolation hole — and a '{'-bearing id could force an unintended hash slot.
    public static string Member(RoomKey room) =>
        $"{Uri.EscapeDataString(room.TenantId.Value)}/{Uri.EscapeDataString(room.RoomId.Value)}";

    public static RedisKey OwnerKey(string prefix, RoomKey room) => $"{prefix}:room:owner:{Member(room)}";

    public static RedisKey NodeRooms(string prefix, NodeId node) => $"{prefix}:node:rooms:{Uri.EscapeDataString(node.Value)}";
}

/// <summary>
/// Cross-node <see cref="IRoomDirectory"/> backed by Redis. Ownership is a fenced, leased key
/// (<c>SET … NX PX</c>) so exactly one node owns a room at a time and a dead node's rooms free
/// themselves when the lease lapses. Per-node room membership is a sorted set scored by lease-expiry,
/// so <see cref="OwnedCount"/> counts only un-expired claims (<c>ZCOUNT now +inf</c>) — no reaper needed
/// for the count to be correct. Claim and release are single Lua scripts, so the owner-key write and the
/// set update are atomic together (server-side time avoids client clock skew).
/// </summary>
public sealed class RedisRoomDirectory : IRoomDirectory
{
    // Trim this node's expired members, then claim (+ZADD by new expiry) when unowned or already ours.
    // Claiming for the current owner refreshes the lease (PX) — this is how a node renews while serving.
    private const string ClaimScript = @"
local t = redis.call('TIME')
local now = (tonumber(t[1]) * 1000) + math.floor(tonumber(t[2]) / 1000)
redis.call('ZREMRANGEBYSCORE', KEYS[2], '-inf', '(' .. now)
local cur = redis.call('GET', KEYS[1])
if cur == false or cur == ARGV[1] then
  redis.call('SET', KEYS[1], ARGV[1], 'PX', tonumber(ARGV[2]))
  redis.call('ZADD', KEYS[2], now + tonumber(ARGV[2]), ARGV[3])
  return 1
end
return 0";

    // Drop expired members first so the count is exactly the live (un-expired) claims and the set
    // cannot grow without bound.
    private const string CountScript = @"
local t = redis.call('TIME')
local now = (tonumber(t[1]) * 1000) + math.floor(tonumber(t[2]) / 1000)
redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', '(' .. now)
return redis.call('ZCARD', KEYS[1])";

    private const string ReleaseScript = @"
if redis.call('GET', KEYS[1]) == ARGV[1] then
  redis.call('DEL', KEYS[1])
  redis.call('ZREM', KEYS[2], ARGV[2])
end
return 1";

    private readonly IDatabase _db;
    private readonly RedisClusterOptions _options;

    public RedisRoomDirectory(IConnectionMultiplexer redis, RedisClusterOptions options)
    {
        _db = redis.GetDatabase();
        _options = options;
    }

    public bool TryClaim(RoomKey room, NodeId owner)
    {
        var result = _db.ScriptEvaluate(
            ClaimScript,
            new[] { RedisClusterKeys.OwnerKey(_options.KeyPrefix, room), RedisClusterKeys.NodeRooms(_options.KeyPrefix, owner) },
            new RedisValue[] { owner.Value, _options.LeaseMs, RedisClusterKeys.Member(room) });
        return (long)result == 1;
    }

    public bool TryGetOwner(RoomKey room, out NodeId owner)
    {
        var value = _db.StringGet(RedisClusterKeys.OwnerKey(_options.KeyPrefix, room));
        if (value.IsNullOrEmpty)
        {
            owner = default;
            return false;
        }

        owner = new NodeId(value.ToString());
        return true;
    }

    public int OwnedCount(NodeId owner)
    {
        var result = _db.ScriptEvaluate(CountScript, new[] { RedisClusterKeys.NodeRooms(_options.KeyPrefix, owner) });
        return (int)(long)result;
    }

    public void Release(RoomKey room, NodeId owner) =>
        _db.ScriptEvaluate(
            ReleaseScript,
            new[] { RedisClusterKeys.OwnerKey(_options.KeyPrefix, room), RedisClusterKeys.NodeRooms(_options.KeyPrefix, owner) },
            new RedisValue[] { owner.Value, RedisClusterKeys.Member(room) });
}
