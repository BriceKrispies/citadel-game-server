using GameServer.Protocol;
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

    /// <summary>
    /// Inverse of <see cref="Member"/>: decodes a per-node set member back to its <see cref="RoomKey"/>.
    /// The encoding is injective (segments are percent-escaped, so the only raw '/' is the separator), so
    /// the split + unescape is unambiguous. Used to enumerate a node's owned rooms for draining.
    /// </summary>
    public static RoomKey ParseMember(string member)
    {
        var slash = member.IndexOf('/');
        if (slash < 0)
        {
            throw new FormatException($"Malformed room directory member '{member}'.");
        }

        var tenant = Uri.UnescapeDataString(member[..slash]);
        var room = Uri.UnescapeDataString(member[(slash + 1)..]);
        return new RoomKey(new TenantId(tenant), new RoomId(room));
    }

    public static RedisKey OwnerKey(string prefix, RoomKey room) => $"{prefix}:room:owner:{Member(room)}";

    public static RedisKey NodeRooms(string prefix, NodeId node) => $"{prefix}:node:rooms:{Uri.EscapeDataString(node.Value)}";

    /// <summary>The draining-flag key for a node. Present (=1) means the node refuses new allocations.</summary>
    public static RedisKey NodeDraining(string prefix, NodeId node) => $"{prefix}:node:draining:{Uri.EscapeDataString(node.Value)}";
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
    // KEYS[3] = the node's draining flag: a draining node may RENEW a room it already owns (so a
    // mid-drain lease does not lapse before the room is shed) but is fenced from taking a NEW room.
    private const string ClaimScript = @"
local t = redis.call('TIME')
local now = (tonumber(t[1]) * 1000) + math.floor(tonumber(t[2]) / 1000)
redis.call('ZREMRANGEBYSCORE', KEYS[2], '-inf', '(' .. now)
local cur = redis.call('GET', KEYS[1])
if cur == ARGV[1] then
  redis.call('SET', KEYS[1], ARGV[1], 'PX', tonumber(ARGV[2]))
  redis.call('ZADD', KEYS[2], now + tonumber(ARGV[2]), ARGV[3])
  return 1
end
if cur == false then
  if redis.call('EXISTS', KEYS[3]) == 1 then return 0 end
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
            new[]
            {
                RedisClusterKeys.OwnerKey(_options.KeyPrefix, room),
                RedisClusterKeys.NodeRooms(_options.KeyPrefix, owner),
                RedisClusterKeys.NodeDraining(_options.KeyPrefix, owner),
            },
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

    public void SetNodeDraining(NodeId node, bool draining)
    {
        var key = RedisClusterKeys.NodeDraining(_options.KeyPrefix, node);
        if (draining)
        {
            // No expiry: a drain decision stays until explicitly cleared (a node coming back into service
            // calls SetNodeDraining(node, false)). A crashed draining node's rooms still free themselves
            // via lease expiry regardless of this flag.
            _db.StringSet(key, "1");
        }
        else
        {
            _db.KeyDelete(key);
        }
    }

    public bool IsNodeDraining(NodeId node) =>
        _db.KeyExists(RedisClusterKeys.NodeDraining(_options.KeyPrefix, node));

    public IReadOnlyCollection<RoomKey> OwnedRooms(NodeId owner)
    {
        // Count by un-expired lease is the source of truth; trim first so a shedding node does not try to
        // release rooms whose lease already lapsed (already reclaimable). Then map members back to keys.
        var setKey = RedisClusterKeys.NodeRooms(_options.KeyPrefix, owner);
        _db.ScriptEvaluate(CountScript, new[] { setKey }); // trims expired members as a side effect
        var members = _db.SortedSetRangeByRank(setKey);
        var rooms = new List<RoomKey>(members.Length);
        foreach (var member in members)
        {
            rooms.Add(RedisClusterKeys.ParseMember(member!));
        }

        return rooms;
    }
}
