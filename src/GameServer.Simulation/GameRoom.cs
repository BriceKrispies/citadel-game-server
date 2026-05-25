using GameServer.Protocol;
using GameServer.Replication;

namespace GameServer.Simulation;

/// <summary>
/// The generic authoritative room host. It owns the platform concerns — command
/// queue, per-player sequence gating, the deterministic clock/random, and the tick
/// loop — and delegates all state and rules to its <see cref="IGameSimulation"/>.
/// State mutates only inside <see cref="Tick"/>. Deterministic: all time comes from
/// the injected <see cref="ISimulationClock"/> and all randomness from the injected
/// <see cref="IRandomSource"/>.
/// </summary>
public sealed class GameRoom : IGameRoom
{
    /// <summary>
    /// Default cap on commands queued between ticks. Sized so a well-behaved client
    /// sending a handful of intents per tick never trips it, while a flooding or
    /// runaway client is shed before the queue can grow without bound.
    /// </summary>
    public const int DefaultMaxQueueDepth = 1024;

    private readonly IGameSimulation _game;
    private readonly ISimulationClock _clock;
    private readonly IRandomSource _random;
    private readonly int _maxQueueDepth;
    private readonly Dictionary<PlayerId, long> _lastSequence = new();
    private readonly Queue<(PlayerId Player, string Command)> _pending = new();

    public GameRoom(RoomId id, IGameSimulation game, ISimulationClock clock, IRandomSource random, int maxQueueDepth = DefaultMaxQueueDepth)
    {
        if (maxQueueDepth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxQueueDepth), maxQueueDepth, "Queue depth must be positive.");
        }

        Id = id;
        _game = game;
        _clock = clock;
        _random = random;
        _maxQueueDepth = maxQueueDepth;
    }

    public RoomId Id { get; }

    /// <summary>Exposed so a game's future stochastic rules stay on the room's deterministic source.</summary>
    public IRandomSource Random => _random;

    /// <summary>Commands currently queued for the next tick. A backpressure gauge for the tick driver.</summary>
    public int QueueDepth => _pending.Count;

    public bool CanJoin(PlayerId player) => _game.CanJoin(player);

    public void Join(PlayerId player) => _game.Join(player);

    public void Leave(PlayerId player) => _game.OnLeave(player);

    public void Terminate() => _game.OnTerminate();

    public bool HasPlayer(PlayerId player) => _game.HasPlayer(player);

    public CommandAdmission TryEnqueue(PlayerId player, string command, long sequence)
    {
        if (!_game.HasPlayer(player))
        {
            return CommandAdmission.RejectedNotJoined;
        }

        // Per-player sequence must strictly increase: anything at or below the last
        // accepted sequence is a stale or duplicated intent and is dropped.
        if (_lastSequence.TryGetValue(player, out var last) && sequence <= last)
        {
            return CommandAdmission.RejectedStaleSequence;
        }

        if (!_game.CanAccept(player, command))
        {
            return CommandAdmission.RejectedInvalidCommand;
        }

        // Backpressure: a bounded queue caps memory and per-tick work. We shed only
        // after the command is otherwise valid, and without recording its sequence, so
        // a legitimate command rejected under load can be retried unchanged once ticks
        // drain the queue.
        if (_pending.Count >= _maxQueueDepth)
        {
            return CommandAdmission.RejectedOverloaded;
        }

        _lastSequence[player] = sequence;
        _pending.Enqueue((player, command));
        return CommandAdmission.Accepted;
    }

    public TickResult Tick()
    {
        var tick = _clock.Advance();

        var events = new List<RoomEvent>(_pending.Count);
        while (_pending.TryDequeue(out var command))
        {
            _game.Apply(command.Player, command.Command);
            events.Add(new RoomEvent(tick, command.Player, command.Command));
        }

        return new TickResult(NewSnapshot(tick), events);
    }

    public IReadOnlyList<EntitySnapshot> Project() => _game.Project();

    public RoomSnapshot Snapshot() => NewSnapshot(_clock.CurrentTick);

    // Capture the replay header (seed + game-schema version) alongside the opaque state, so a
    // FRESH process can re-seed an identical random source and replay post-snapshot events to the
    // identical state. The seed comes from THIS room's deterministic source — never wall-clock.
    private RoomSnapshot NewSnapshot(long tick) =>
        new(tick, _game.Serialize(), _random.Seed, _game.SchemaVersion);

    public void RestoreFrom(RoomSnapshot snapshot)
    {
        _game.Restore(snapshot.State);

        // Re-establish the deterministic random sequence from the captured seed so any stochastic
        // rule replays identically on this (possibly fresh) process. A legacy snapshot without a
        // header (Seed == 0) leaves the source on its construction seed — the pre-header behavior.
        if (snapshot.Seed != 0)
        {
            _random.Reseed(snapshot.Seed);
        }

        // A restored room starts with no pending input; per-player sequence gating
        // restarts from the recovered baseline (sequence state is not snapshotted).
        _pending.Clear();
        _lastSequence.Clear();
        _clock.Reset(snapshot.Tick);
    }

    public bool ApplyRecoveredEvent(RoomEvent recoveredEvent)
    {
        // The player may have joined after the snapshot; ensure they exist before
        // replaying their command effect.
        if (!_game.HasPlayer(recoveredEvent.Player))
        {
            _game.Join(recoveredEvent.Player);
        }

        if (!_game.CanAccept(recoveredEvent.Player, recoveredEvent.Command))
        {
            return false;
        }

        _game.Apply(recoveredEvent.Player, recoveredEvent.Command);
        _clock.Reset(recoveredEvent.Tick);
        return true;
    }
}

/// <summary>
/// Creates a room for a given <see cref="RoomId"/> and <see cref="GameId"/>. The
/// factory is where the room's game (per the catalog) and its deterministic
/// clock/random are bound, so the routing layer never needs to know either.
/// </summary>
public delegate IGameRoom GameRoomFactory(RoomId id, GameId gameId);
