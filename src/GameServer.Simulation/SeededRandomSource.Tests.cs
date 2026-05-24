using Xunit;

namespace GameServer.Simulation;

public sealed class SeededRandomSourceTests
{
    [Fact]
    public void SameSeed_ProducesSameSequence()
    {
        var a = new SeededRandomSource(seed: 123);
        var b = new SeededRandomSource(seed: 123);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(a.Next(1000), b.Next(1000));
        }
    }

    [Fact]
    public void Next_StaysWithinBound()
    {
        var random = new SeededRandomSource(seed: 7);

        for (var i = 0; i < 100; i++)
        {
            var value = random.Next(10);
            Assert.InRange(value, 0, 9);
        }
    }
}
