using Xunit;

namespace GameServer.Replication;

public sealed class PriorityAccumulatorTests
{
    [Fact]
    public void Priority_DeferredEntity_EscalatesThenResetsWhenSent()
    {
        var accumulator = new PriorityAccumulator();
        var viewer = new ViewerId("v");
        var entity = new EntityId("e");

        Assert.Equal(2, accumulator.Priority(viewer, entity, basePriority: 2)); // fresh: base only (non-1 base)

        accumulator.OnDeferred(viewer, entity);
        accumulator.OnDeferred(viewer, entity);
        accumulator.OnDeferred(viewer, entity);

        Assert.Equal(5, accumulator.Priority(viewer, entity, basePriority: 2)); // base 2 + 3 staleness

        accumulator.OnSent(viewer, entity);

        Assert.Equal(2, accumulator.Priority(viewer, entity, basePriority: 2)); // reset after send
    }

    [Fact]
    public void Priority_StalenessIsIsolatedPerViewerAndEntity()
    {
        var accumulator = new PriorityAccumulator();
        var v1 = new ViewerId("v1");
        var v2 = new ViewerId("v2");
        var entityA = new EntityId("a");
        var entityB = new EntityId("b");

        accumulator.OnDeferred(v1, entityA);
        accumulator.OnDeferred(v1, entityA);
        accumulator.OnDeferred(v1, entityA);

        Assert.Equal(4, accumulator.Priority(v1, entityA, basePriority: 1)); // escalated
        Assert.Equal(1, accumulator.Priority(v1, entityB, basePriority: 1)); // different entity unaffected
        Assert.Equal(1, accumulator.Priority(v2, entityA, basePriority: 1)); // different viewer unaffected
    }
}
