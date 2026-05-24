using GameServer.Routing;
using StackExchange.Redis;

namespace GameServer.Cluster.Redis;

/// <summary>
/// Cross-node <see cref="IRoomPlacement"/> backed by Redis. The whole decision — idempotency check,
/// per-node capacity check, fenced claim, and set bookkeeping — runs as a SINGLE Lua script, so it is
/// atomic across the entire fleet (unlike <c>CapacityAwareRoomPlacement</c>, whose per-process lock
/// cannot serialize placement happening on other nodes). Capacity is counted by un-expired lease, so a
/// dead node's slots free up without a reaper.
/// </summary>
public sealed class RedisRoomPlacement : IRoomPlacement
{
    // ARGV[1]=member, ARGV[2]=cap, ARGV[3]=leaseMs, ARGV[4..]=node names; KEYS[1]=ownerKey, KEYS[2..]=per-node sets.
    private const string PlaceScript = @"
local cur = redis.call('GET', KEYS[1])
if cur ~= false then return cur end
local t = redis.call('TIME')
local now = (tonumber(t[1]) * 1000) + math.floor(tonumber(t[2]) / 1000)
local cap = tonumber(ARGV[2])
local lease = tonumber(ARGV[3])
local n = #KEYS - 1
for i = 1, n do
  local zkey = KEYS[i + 1]
  local node = ARGV[i + 3]
  if redis.call('ZCOUNT', zkey, now, '+inf') < cap then
    redis.call('SET', KEYS[1], node, 'PX', lease)
    redis.call('ZADD', zkey, now + lease, ARGV[1])
    return node
  end
end
return ''";

    private readonly IDatabase _db;
    private readonly RedisClusterOptions _options;
    private readonly IReadOnlyList<NodeId> _nodes;
    private readonly int _maxRoomsPerNode;

    public RedisRoomPlacement(IConnectionMultiplexer redis, IReadOnlyList<NodeId> nodes, int maxRoomsPerNode, RedisClusterOptions options)
    {
        if (nodes is null || nodes.Count == 0)
        {
            throw new ArgumentException("At least one node is required.", nameof(nodes));
        }

        if (maxRoomsPerNode < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRoomsPerNode), maxRoomsPerNode, "Capacity must be >= 1.");
        }

        _db = redis.GetDatabase();
        _options = options;
        _nodes = nodes;
        _maxRoomsPerNode = maxRoomsPerNode;
    }

    public RoomPlacementResult Place(RoomKey room)
    {
        var keys = new RedisKey[_nodes.Count + 1];
        keys[0] = RedisClusterKeys.OwnerKey(_options.KeyPrefix, room);
        for (var i = 0; i < _nodes.Count; i++)
        {
            keys[i + 1] = RedisClusterKeys.NodeRooms(_options.KeyPrefix, _nodes[i]);
        }

        var values = new RedisValue[_nodes.Count + 3];
        values[0] = RedisClusterKeys.Member(room);
        values[1] = _maxRoomsPerNode;
        values[2] = _options.LeaseMs;
        for (var i = 0; i < _nodes.Count; i++)
        {
            values[i + 3] = _nodes[i].Value;
        }

        var owner = (string?)_db.ScriptEvaluate(PlaceScript, keys, values);
        return string.IsNullOrEmpty(owner)
            ? RoomPlacementResult.ClusterAtCapacity
            : RoomPlacementResult.OnNode(new NodeId(owner));
    }
}
