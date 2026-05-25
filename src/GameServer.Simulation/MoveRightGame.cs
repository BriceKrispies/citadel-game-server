using System.Buffers.Binary;
using System.Text.Json;
using GameServer.Protocol;
using GameServer.Replication;

namespace GameServer.Simulation;

/// <summary>
/// The reference game: every player has an integer X that the single command
/// "MoveRight" increments. Deliberately the simplest possible authoritative state —
/// its job is to exercise the platform end to end, not to be interesting.
/// </summary>
public sealed class MoveRightGame : IGameSimulation
{
    public const string MoveRight = "MoveRight";

    private readonly Dictionary<PlayerId, int> _x = new();
    private readonly List<PlayerId> _left = new();

    /// <summary>
    /// Optional per-room player capacity. 0 = unbounded (the original behavior, so existing
    /// callers are unaffected). When set, <see cref="CanJoin"/> refuses a join that would
    /// exceed it — a sample game enforcing its own admission rule (distinct from the platform's).
    /// </summary>
    public int MaxPlayers { get; init; }

    /// <summary>Players the game observed leaving, in order — proof a game can react to <c>OnLeave</c>.</summary>
    public IReadOnlyList<PlayerId> LeftPlayers => _left;

    /// <summary>True once the game observed the room terminating — proof a game can react to <c>OnTerminate</c>.</summary>
    public bool Terminated { get; private set; }

    public bool CanJoin(PlayerId player) =>
        MaxPlayers <= 0 || _x.ContainsKey(player) || _x.Count < MaxPlayers;

    public void Join(PlayerId player) => _x.TryAdd(player, 0);

    // The reference game records the leave (so a test/observer can see the hook fire) but keeps
    // the player's authoritative position, so a reconnect resumes where it left off. Whether to
    // discard a leaver's state is a per-game decision; the platform never forces it.
    public void OnLeave(PlayerId player) => _left.Add(player);

    public void OnTerminate() => Terminated = true;

    public bool HasPlayer(PlayerId player) => _x.ContainsKey(player);

    public bool CanAccept(PlayerId player, string command) => command == MoveRight;

    public void Apply(PlayerId player, string command)
    {
        if (command != MoveRight)
        {
            throw new ArgumentException($"Unknown command '{command}'.", nameof(command));
        }

        _x[player] += 1;
    }

    public IReadOnlyList<EntitySnapshot> Project()
    {
        var entities = new List<EntitySnapshot>(_x.Count);
        foreach (var (player, x) in _x)
        {
            // Version = x: it only ever increases, so any move is a detectable change.
            // Relevance key carries the 1-D position for spatial interest policies.
            entities.Add(new EntitySnapshot(new EntityId(player.Value), x, new RelevanceKey(x, 0, string.Empty), EncodeX(x)));
        }

        return entities;
    }

    public byte[] Serialize() =>
        JsonSerializer.SerializeToUtf8Bytes(_x.ToDictionary(kv => kv.Key.Value, kv => kv.Value));

    public void Restore(byte[] state)
    {
        _x.Clear();
        var positions = JsonSerializer.Deserialize<Dictionary<string, int>>(state) ?? new();
        foreach (var (player, x) in positions)
        {
            _x[new PlayerId(player)] = x;
        }
    }

    /// <summary>The wire payload for a player entity: little-endian int32 X.</summary>
    public static byte[] EncodeX(int x)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(payload, x);
        return payload;
    }

    /// <summary>Decodes a player entity payload back into X (for clients/tests).</summary>
    public static int DecodeX(byte[] payload) => BinaryPrimitives.ReadInt32LittleEndian(payload);
}
