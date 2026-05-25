using Xunit;

namespace GameServer.Routing;

/// <summary>
/// Pins the room allocation lifecycle state machine: only the legal transitions are admitted, and every
/// illegal one is rejected loudly. This is the contract the allocator and drain mechanism rely on to keep
/// ownership accounting consistent (a room can never, e.g., jump from Released straight to Allocated).
/// </summary>
public sealed class RoomAllocationLifecycleTests
{
    [Theory]
    [InlineData(RoomAllocationState.Reserved, RoomAllocationState.Allocated)]
    [InlineData(RoomAllocationState.Reserved, RoomAllocationState.Released)]
    [InlineData(RoomAllocationState.Allocated, RoomAllocationState.Draining)]
    [InlineData(RoomAllocationState.Allocated, RoomAllocationState.Released)]
    [InlineData(RoomAllocationState.Draining, RoomAllocationState.Released)]
    [InlineData(RoomAllocationState.Released, RoomAllocationState.Reserved)]
    public void Legal_Transitions_AreAdmitted(RoomAllocationState from, RoomAllocationState to)
    {
        Assert.True(RoomAllocationLifecycle.CanTransition(from, to));
        Assert.Equal(to, RoomAllocationLifecycle.Transition(from, to));
    }

    [Theory]
    [InlineData(RoomAllocationState.Reserved, RoomAllocationState.Draining)]   // can't drain before live
    [InlineData(RoomAllocationState.Released, RoomAllocationState.Allocated)]  // can't go live without reserving
    [InlineData(RoomAllocationState.Released, RoomAllocationState.Draining)]
    [InlineData(RoomAllocationState.Allocated, RoomAllocationState.Reserved)]  // can't un-allocate to reserved
    [InlineData(RoomAllocationState.Draining, RoomAllocationState.Allocated)]  // a draining room won't go live again here
    public void Illegal_Transitions_AreRejected(RoomAllocationState from, RoomAllocationState to)
    {
        Assert.False(RoomAllocationLifecycle.CanTransition(from, to));
        Assert.Throws<InvalidOperationException>(() => RoomAllocationLifecycle.Transition(from, to));
    }

    [Fact]
    public void Identity_Transitions_AreNotLegal()
    {
        // A no-op self-transition is not a lifecycle move; callers should not advance to the same state.
        foreach (var state in Enum.GetValues<RoomAllocationState>())
        {
            Assert.False(RoomAllocationLifecycle.CanTransition(state, state));
        }
    }

    [Fact]
    public void DrainedRoom_ReentersLifecycle_AsReservedOnNewOwner()
    {
        // The migration cycle: a live room drains off its old owner, is released there, then re-reserves
        // on its new owner and goes live again. This sequence must be entirely legal.
        var s = RoomAllocationState.Allocated;
        s = RoomAllocationLifecycle.Transition(s, RoomAllocationState.Draining);
        s = RoomAllocationLifecycle.Transition(s, RoomAllocationState.Released);
        s = RoomAllocationLifecycle.Transition(s, RoomAllocationState.Reserved);
        s = RoomAllocationLifecycle.Transition(s, RoomAllocationState.Allocated);
        Assert.Equal(RoomAllocationState.Allocated, s);
    }
}
