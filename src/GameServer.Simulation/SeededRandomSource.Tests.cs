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

    [Fact]
    public void CaptureState_AfterDraws_RestoresExactMidSequencePosition()
    {
        var random = new SeededRandomSource(seed: 123);

        // Advance into the sequence, then capture the state at this mid-point.
        for (var i = 0; i < 4; i++)
        {
            random.Next(1000);
        }

        var captured = random.CaptureState();
        var expected = new[] { random.Next(1000), random.Next(1000), random.Next(1000) };

        // Restoring the captured state must reproduce the continuation EXACTLY — not the seed start.
        random.RestoreState(captured);
        var afterRestore = new[] { random.Next(1000), random.Next(1000), random.Next(1000) };

        Assert.Equal(expected, afterRestore);
    }

    [Fact]
    public void Next_NonPositiveBound_Throws()
    {
        var random = new SeededRandomSource(seed: 7);

        // The boundary itself (0) and any negative bound are illegal — not just "< 0".
        Assert.Throws<ArgumentOutOfRangeException>(() => random.Next(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => random.Next(-1));
    }

    [Fact]
    public void Seed_ProducesAGoldenSequence_PinningTheExactAlgorithm()
    {
        // Golden values captured from the SplitMix64 implementation. This is the only test that
        // pins the EXACT bit-mixing and state advance: same-seed-same-sequence and capture/restore
        // all stay green even if the algorithm is altered consistently, so without a golden anchor a
        // changed gamma step or shift/xor still "agrees with itself". These numbers lock the algorithm
        // (and thus cross-process/replay reproducibility) to one specific, durable sequence.
        var random = new SeededRandomSource(seed: 123);

        var sequence = new long[8];
        for (var i = 0; i < sequence.Length; i++)
        {
            sequence[i] = random.Next(1_000_000);
        }

        Assert.Equal(
            new long[] { 706491, 976596, 859662, 686798, 686085, 667090, 999939, 482356 },
            sequence);
    }

    [Fact]
    public void CapturedState_RestoresOntoADifferentSource_YieldingTheSameContinuation()
    {
        // The cross-process replay case: a fresh source (different seed) adopts a captured state and
        // continues the identical sequence — exactly how a rewound/replayed room resumes the RNG.
        var original = new SeededRandomSource(seed: 31337);
        for (var i = 0; i < 5; i++)
        {
            original.Next(1000);
        }

        var captured = original.CaptureState();
        var expected = new[] { original.NextDouble(), original.NextDouble() };

        var fresh = new SeededRandomSource(seed: 1); // deliberately different construction seed
        fresh.RestoreState(captured);
        var continued = new[] { fresh.NextDouble(), fresh.NextDouble() };

        Assert.Equal(expected, continued);
    }
}
