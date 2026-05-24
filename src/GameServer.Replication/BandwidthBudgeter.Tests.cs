using Xunit;

namespace GameServer.Replication;

public sealed class BandwidthBudgeterTests
{
    private static ReplicationCandidate Candidate(string id, int sizeBytes, int priority) =>
        new(new EntitySnapshot(new EntityId(id), Version: 1, RelevanceKey.None, new byte[sizeBytes]), priority);

    private static string[] RawIds(IEnumerable<EntitySnapshot> entities) =>
        entities.Select(e => e.Id.Value).ToArray();

    [Fact]
    public void Budget_LimitsBytes_AndTieBreaksByInputOrder()
    {
        var budgeter = new BandwidthBudgeter();
        var candidates = new[]
        {
            Candidate("a", sizeBytes: 10, priority: 1),
            Candidate("b", sizeBytes: 10, priority: 1),
            Candidate("c", sizeBytes: 10, priority: 1),
        };

        var selection = budgeter.Select(candidates, budgetBytes: 25);

        // 10 + 10 fit; the third would exceed. Equal priority → input order decides: a, b kept; c deferred.
        Assert.Equal(new[] { "a", "b" }, RawIds(selection.Sent));
        Assert.Equal("c", Assert.Single(selection.Deferred).Id.Value);
    }

    [Fact]
    public void Budget_SendsHighestPriorityFirst()
    {
        var budgeter = new BandwidthBudgeter();
        var candidates = new[]
        {
            Candidate("low", sizeBytes: 10, priority: 1),
            Candidate("high", sizeBytes: 10, priority: 5),
            Candidate("mid", sizeBytes: 10, priority: 3),
        };

        var selection = budgeter.Select(candidates, budgetBytes: 10); // room for exactly one

        Assert.Equal("high", Assert.Single(selection.Sent).Id.Value);
        Assert.Equal(2, selection.Deferred.Count);
        Assert.Contains(selection.Deferred, e => e.Id.Value == "mid");
        Assert.Contains(selection.Deferred, e => e.Id.Value == "low");
    }

    [Fact]
    public void Budget_ExactFit_IsInclusive()
    {
        var budgeter = new BandwidthBudgeter();
        var candidates = new[] { Candidate("a", 10, 1), Candidate("b", 10, 1) };

        var selection = budgeter.Select(candidates, budgetBytes: 20); // exactly fits both

        Assert.Equal(2, selection.Sent.Count);
        Assert.Empty(selection.Deferred);
    }

    [Fact]
    public void Budget_SingleItemLargerThanBudget_IsDeferred()
    {
        var budgeter = new BandwidthBudgeter();
        var candidates = new[] { Candidate("big", sizeBytes: 100, priority: 9) };

        var selection = budgeter.Select(candidates, budgetBytes: 10);

        Assert.Empty(selection.Sent);
        Assert.Equal("big", Assert.Single(selection.Deferred).Id.Value);
    }

    [Fact]
    public void Budget_EmptyCandidates_ProducesEmptySelection()
    {
        var selection = new BandwidthBudgeter().Select(Array.Empty<ReplicationCandidate>(), budgetBytes: 100);

        Assert.Empty(selection.Sent);
        Assert.Empty(selection.Deferred);
    }
}
