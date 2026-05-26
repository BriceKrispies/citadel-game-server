using Xunit;

namespace GameServer.Transport;

public sealed class TickGateTests
{
    [Fact]
    public void Cycles_RunFreely_WhenNotPaused()
    {
        var gate = new TickGate();

        Assert.True(gate.TryBeginCycle());
        gate.EndCycle();
        Assert.False(gate.IsPaused);
    }

    [Fact]
    public void Pause_BlocksNewCycles_UntilResumed()
    {
        var gate = new TickGate();

        var scope = gate.Pause();
        Assert.True(gate.IsPaused);
        Assert.False(gate.TryBeginCycle()); // paused: cycle is skipped

        scope.Dispose();
        Assert.False(gate.IsPaused);
        Assert.True(gate.TryBeginCycle()); // resumed: cycles run again
        gate.EndCycle();
    }

    [Fact]
    public async Task Pause_WaitsForInFlightCycleToDrain()
    {
        var gate = new TickGate();
        Assert.True(gate.TryBeginCycle()); // a cycle is in flight

        var paused = false;
        var pauseTask = Task.Run(() =>
        {
            using (gate.Pause())
            {
                paused = true;
            }
        });

        // The pause cannot complete while the cycle is in flight: the delay wins the race.
        var first = await Task.WhenAny(pauseTask, Task.Delay(TimeSpan.FromMilliseconds(100)));
        Assert.NotSame(pauseTask, first);
        Assert.False(paused);

        gate.EndCycle();          // cycle drains
        await pauseTask;          // now the pause acquires
        Assert.True(paused);
    }

    [Fact]
    public void Pause_IsStackable_ResumesOnlyWhenLastScopeDisposed()
    {
        var gate = new TickGate();

        var outer = gate.Pause();
        var inner = gate.Pause();

        inner.Dispose();
        Assert.True(gate.IsPaused); // outer still holds it

        outer.Dispose();
        Assert.False(gate.IsPaused);
    }

    [Fact]
    public void PauseScope_DisposeIsIdempotent()
    {
        var gate = new TickGate();
        var scope = gate.Pause();

        scope.Dispose();
        scope.Dispose(); // must not underflow the pause depth

        Assert.False(gate.IsPaused);
    }
}
