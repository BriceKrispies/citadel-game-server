using GameServer.Routing;
using StackExchange.Redis;

namespace GameServer.Cluster.Redis;

/// <summary>
/// Cross-node <see cref="IRoomPlacement"/> backed by Redis. The whole decision — idempotency check,
/// per-node capacity check, fenced claim, and set bookkeeping — runs as a SINGLE Lua script, so it is
/// atomic across the entire fleet (unlike <c>CapacityAwareRoomPlacement</c>, whose per-process lock
/// cannot serialize placement happening on other nodes). Capacity is counted by un-expired lease, so a
/// dead node's slots free up without a reaper. A node is filled only up to its allocatable ceiling
/// (<c>maxRoomsPerNode - reservedHeadroom</c>), keeping spare slots for shed/migrated rooms, and a
/// draining node is skipped entirely.
/// </summary>
public sealed class RedisRoomPlacement : IRoomPlacement
{
    // ARGV[1]=member, ARGV[2]=allocatableCeiling, ARGV[3]=leaseMs, ARGV[4..4+n-1]=node names;
    // KEYS[1]=ownerKey, KEYS[2..2+n-1]=per-node room sets, KEYS[2+n..]=per-node draining flags (same order).
    private const string PlaceScript = @"
local n = (#KEYS - 1) / 2
local cur = redis.call('GET', KEYS[1])
local curIndex = 0
if cur ~= false then
  -- Idempotent only for a LIVE, NON-DRAINING owner. If the room is owned by a node not in the current
  -- fleet (scaled down / replaced) OR by a node that is draining, the claim is STALE and must move — but
  -- we defer dropping it until a live target is secured, so a room with nowhere to re-home keeps its owner
  -- (never stranded). curIndex remembers a draining owner so we can shed its set membership on the move.
  for i = 1, n do
    if ARGV[i + 3] == cur then
      if redis.call('EXISTS', KEYS[i + 1 + n]) == 0 then return cur end
      curIndex = i
      break
    end
  end
end
local t = redis.call('TIME')
local now = (tonumber(t[1]) * 1000) + math.floor(tonumber(t[2]) / 1000)
local cap = tonumber(ARGV[2])
local lease = tonumber(ARGV[3])
for i = 1, n do
  local zkey = KEYS[i + 1]
  local drainKey = KEYS[i + 1 + n]
  redis.call('ZREMRANGEBYSCORE', zkey, '-inf', '(' .. now)
  -- Skip a draining node (refuses new rooms) and any node at its allocatable ceiling.
  if redis.call('EXISTS', drainKey) == 0 and redis.call('ZCARD', zkey) < cap then
    -- Live target found: NOW move off the stale owner (drop its set membership) and re-claim. The whole
    -- script is atomic, so there is no observable window in which the room is owner-less or double-owned.
    if curIndex > 0 then redis.call('ZREM', KEYS[curIndex + 1], ARGV[1]) end
    local node = ARGV[i + 3]
    redis.call('SET', KEYS[1], node, 'PX', lease)
    redis.call('ZADD', zkey, now + lease, ARGV[1])
    return node
  end
end
-- No live target: leave the (possibly stale/draining) owner in place and report cluster-at-capacity.
return ''";

    private readonly IDatabase _db;
    private readonly RedisClusterOptions _options;
    private readonly IReadOnlyList<NodeId> _nodes;
    private readonly int _allocatableCeiling;

    public RedisRoomPlacement(
        IConnectionMultiplexer redis,
        IReadOnlyList<NodeId> nodes,
        int maxRoomsPerNode,
        RedisClusterOptions options,
        int reservedHeadroom = 0)
    {
        if (nodes is null || nodes.Count == 0)
        {
            throw new ArgumentException("At least one node is required.", nameof(nodes));
        }

        if (maxRoomsPerNode < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRoomsPerNode), maxRoomsPerNode, "Capacity must be >= 1.");
        }

        if (reservedHeadroom < 0 || reservedHeadroom >= maxRoomsPerNode)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reservedHeadroom), reservedHeadroom,
                "Reserved headroom must be >= 0 and leave at least one allocatable slot (< maxRoomsPerNode).");
        }

        _db = redis.GetDatabase();
        _options = options;
        _nodes = nodes;
        _allocatableCeiling = maxRoomsPerNode - reservedHeadroom;
    }

    public RoomPlacementResult Place(RoomKey room)
    {
        var keys = new RedisKey[(_nodes.Count * 2) + 1];
        keys[0] = RedisClusterKeys.OwnerKey(_options.KeyPrefix, room);
        for (var i = 0; i < _nodes.Count; i++)
        {
            keys[i + 1] = RedisClusterKeys.NodeRooms(_options.KeyPrefix, _nodes[i]);
            keys[i + 1 + _nodes.Count] = RedisClusterKeys.NodeDraining(_options.KeyPrefix, _nodes[i]);
        }

        var values = new RedisValue[_nodes.Count + 3];
        values[0] = RedisClusterKeys.Member(room);
        values[1] = _allocatableCeiling;
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
