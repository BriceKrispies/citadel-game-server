namespace GameServer.Transport;

/// <summary>
/// A quiesce gate between the authoritative tick driver and maintenance operations that must not run
/// concurrently with ticking (notably bulk/single room rewind, which swaps room state). The tick driver
/// brackets each cycle with <see cref="TryBeginCycle"/>/<see cref="EndCycle"/>; a maintenance operation
/// calls <see cref="Pause"/>, which blocks until any in-flight cycle drains and then keeps new cycles
/// out until the returned scope is disposed. This is what lets a rewind swap a room's state with the
/// guarantee that no tick is reading or advancing it.
/// </summary>
public sealed class TickGate
{
    private readonly object _sync = new();
    private int _activeCycles;
    private int _pauseDepth;

    /// <summary>
    /// Attempts to begin a tick cycle. Returns true and counts the cycle as active when not paused;
    /// returns false when a pause is in effect (the driver should skip ticking this cycle). Every true
    /// result must be matched by exactly one <see cref="EndCycle"/>.
    /// </summary>
    public bool TryBeginCycle()
    {
        lock (_sync)
        {
            if (_pauseDepth > 0)
            {
                return false;
            }

            _activeCycles++;
            return true;
        }
    }

    /// <summary>Marks a cycle begun via <see cref="TryBeginCycle"/> as finished.</summary>
    public void EndCycle()
    {
        lock (_sync)
        {
            _activeCycles--;
            Monitor.PulseAll(_sync);
        }
    }

    /// <summary>
    /// Blocks until no tick cycle is in flight, then holds off new cycles until the returned scope is
    /// disposed. Reentrant/stackable: nested pauses are reference-counted so ticking resumes only when
    /// the last scope is disposed.
    /// </summary>
    public IDisposable Pause()
    {
        lock (_sync)
        {
            _pauseDepth++;
            while (_activeCycles > 0)
            {
                Monitor.Wait(_sync);
            }
        }

        return new PauseScope(this);
    }

    /// <summary>True while at least one pause scope is held (the tick driver should not tick).</summary>
    public bool IsPaused
    {
        get
        {
            lock (_sync)
            {
                return _pauseDepth > 0;
            }
        }
    }

    private void Resume()
    {
        lock (_sync)
        {
            _pauseDepth--;
            Monitor.PulseAll(_sync);
        }
    }

    private sealed class PauseScope : IDisposable
    {
        private TickGate? _gate;

        public PauseScope(TickGate gate) => _gate = gate;

        public void Dispose()
        {
            // Idempotent: only the first dispose resumes, so a using-scope disposed twice cannot
            // underflow the pause depth.
            var gate = Interlocked.Exchange(ref _gate, null);
            gate?.Resume();
        }
    }
}
