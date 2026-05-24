using System.Diagnostics;
using GameServer.Protocol;
using GameServer.Replication;
using GameServer.Simulation;

namespace GameServer.IntegrationTests;

/// <summary>
/// A game whose per-tick cost is a configurable amount of real CPU work, so a scenario
/// can make ticking a room measurably expensive and observe how rooms are scheduled. The
/// cost is burned in <see cref="Serialize"/> (called once per <c>Tick</c>) via a busy
/// spin, which keeps a core occupied like genuine per-tick simulation work would.
/// </summary>
public sealed class BusyGame : IGameSimulation
{
    private readonly double _costMs;
    private readonly HashSet<PlayerId> _players = new();

    public BusyGame(double costMs) => _costMs = costMs;

    public void Join(PlayerId player) => _players.Add(player);

    public bool HasPlayer(PlayerId player) => _players.Contains(player);

    public bool CanAccept(PlayerId player, string command) => true;

    public void Apply(PlayerId player, string command) { }

    public IReadOnlyList<EntitySnapshot> Project() => _players
        .Select(p => new EntitySnapshot(new EntityId(p.Value), 1, RelevanceKey.None, Array.Empty<byte>()))
        .ToList();

    public byte[] Serialize()
    {
        SpinFor(_costMs);
        return Array.Empty<byte>();
    }

    public void Restore(byte[] state) { }

    private static void SpinFor(double milliseconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalMilliseconds < milliseconds)
        {
            Thread.SpinWait(64);
        }
    }
}
