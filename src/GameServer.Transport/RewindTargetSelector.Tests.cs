using GameServer.Protocol;
using GameServer.Routing;
using Xunit;

namespace GameServer.Transport;

public sealed class RewindTargetSelectorTests
{
    private static RoomKey Key => new(new TenantId("t"), new RoomId("r"));

    [Fact]
    public void RewindToTick_ClampsToCurrent_NeverForward()
    {
        var selector = new RewindToTick(5);

        Assert.Equal(5, selector.TargetTickFor(Key, currentTick: 10)); // within history
        Assert.Equal(3, selector.TargetTickFor(Key, currentTick: 3));  // never ahead of current
    }

    [Fact]
    public void RewindByTicks_SubtractsFromEachRoomsOwnTick_FlooredAtZero()
    {
        var selector = new RewindByTicks(4);

        Assert.Equal(6, selector.TargetTickFor(Key, currentTick: 10));
        Assert.Equal(0, selector.TargetTickFor(Key, currentTick: 2)); // floored, not negative
    }

    [Fact]
    public void NegativeArguments_AreTreatedAsZero()
    {
        Assert.Equal(0, new RewindToTick(-5).TargetTickFor(Key, currentTick: 10));
        Assert.Equal(10, new RewindByTicks(-5).TargetTickFor(Key, currentTick: 10));
    }
}
