using System.Buffers.Binary;
using System.Text.Json;
using GameServer.Protocol;
using GameServer.Replication;

namespace GameServer.Simulation;

/// <summary>
/// A second reference game whose state differs from <see cref="MoveRightGame"/>: each
/// player has a 2-D grid position moved by "Up"/"Down"/"Left"/"Right". Its purpose is
/// to prove the platform hosts arbitrary game state, command vocabulary, and spatial
/// relevance keys without understanding any of them.
/// </summary>
public sealed class GridWalkGame : IGameSimulation
{
    public const string Up = "Up";
    public const string Down = "Down";
    public const string Left = "Left";
    public const string Right = "Right";

    private readonly Dictionary<PlayerId, Walker> _walkers = new();

    public void Join(PlayerId player) => _walkers.TryAdd(player, new Walker(0, 0, 0));

    public bool HasPlayer(PlayerId player) => _walkers.ContainsKey(player);

    public bool CanAccept(PlayerId player, string command) => command is Up or Down or Left or Right;

    public void Apply(PlayerId player, string command)
    {
        var w = _walkers[player];
        var moved = command switch
        {
            Up => w with { Y = w.Y + 1 },
            Down => w with { Y = w.Y - 1 },
            Left => w with { X = w.X - 1 },
            Right => w with { X = w.X + 1 },
            _ => throw new ArgumentException($"Unknown command '{command}'.", nameof(command)),
        };

        // Bump the version on every move so the replication layer detects the change
        // (position alone is not monotonic — a walker can return to a prior cell).
        _walkers[player] = moved with { Version = w.Version + 1 };
    }

    public IReadOnlyList<EntitySnapshot> Project()
    {
        var entities = new List<EntitySnapshot>(_walkers.Count);
        foreach (var (player, w) in _walkers)
        {
            entities.Add(new EntitySnapshot(
                new EntityId(player.Value), w.Version, new RelevanceKey(w.X, w.Y, string.Empty), Encode(w.X, w.Y)));
        }

        return entities;
    }

    public byte[] Serialize() =>
        JsonSerializer.SerializeToUtf8Bytes(
            _walkers.ToDictionary(kv => kv.Key.Value, kv => new long[] { kv.Value.X, kv.Value.Y, kv.Value.Version }));

    public void Restore(byte[] state)
    {
        _walkers.Clear();
        var restored = JsonSerializer.Deserialize<Dictionary<string, long[]>>(state) ?? new();
        foreach (var (player, v) in restored)
        {
            _walkers[new PlayerId(player)] = new Walker((int)v[0], (int)v[1], v[2]);
        }
    }

    /// <summary>The wire payload for a walker entity: little-endian int32 X then int32 Y.</summary>
    public static byte[] Encode(int x, int y)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(payload, x);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), y);
        return payload;
    }

    /// <summary>Decodes a walker entity payload back into (X, Y) (for clients/tests).</summary>
    public static (int X, int Y) Decode(byte[] payload) =>
        (BinaryPrimitives.ReadInt32LittleEndian(payload), BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4)));

    private sealed record Walker(int X, int Y, long Version);
}
