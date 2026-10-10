using AgentEval.Results.Checkpoints;
using AgentEval.Results.Signatures;
using AgentEval.Results.Tests.Corpus;

namespace AgentEval.Results.Tests.Checkpoints;

/// <summary>
/// [CKP-8], [STRM-4], [RUN-1], [RUN-13]: runs are found by their run.json, by <c>runId</c> and run hash ([SEAL-4]), and
/// a run held by several folders is intact when any copy is, whatever the order the folders are listed in.
/// </summary>
public sealed class AefRunStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"aef-store-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("a-tampered", "b-original")]
    [InlineData("z-tampered", "b-original")]
    public void ARunHeldByTwoFolders_IsIntactWhenEitherIs_WhateverTheirOrder(string tamperedFolder, string originalFolder)
    {
        var run = new LaneRunBuilder("G").Score("c1", 0.9).Entry("quality", "m", "p");
        var original = run.Write(_root, originalFolder);
        var copy = run.Write(_root, tamperedFolder);
        File.AppendAllText(Path.Combine(copy, "metrics.json"), " ");   // edited after sealing: the seal still names the old run hash
        var hash = LaneRunBuilder.RunHashOf(original);

        var store = AefRunStore.Open(_root);
        Assert.Equal(2, store.WithRunId("G").Count);
        Assert.All(store.WithRunId("G"), r => Assert.Equal(hash, r.RunHash.Value));

        var found = store.Find("G", hash)!;
        Assert.True(found.Intact);
        Assert.Equal(original, found.Directory);
        Assert.Same(found, store.FindIntact("G", hash));
    }

    [Fact]
    public void ARunHeldOnlyByCopiesThatAreNotIntact_IsFound_AndNotIntact()
    {
        var run = new LaneRunBuilder("G").Score("c1", 0.9).Entry("quality", "m", "p");
        var a = run.Write(_root, "a");
        var b = run.Write(_root, "b");
        var hash = LaneRunBuilder.RunHashOf(a);
        File.AppendAllText(Path.Combine(a, "metrics.json"), " ");
        File.AppendAllText(Path.Combine(b, "metrics.json"), "  ");

        var store = AefRunStore.Open(_root);
        var found = store.Find("G", hash)!;
        Assert.False(found.Intact);
        Assert.Equal(a, found.Directory);   // the first in path order (W4-4)
        Assert.Null(store.FindIntact("G", hash));
    }

    [Fact]
    public void TwoRunsWithOneRunId_AreDifferentRuns_FoundByTheirRunHashes()
    {
        // [RUN-13]: the same runId and different run hashes are different runs.
        var first = new LaneRunBuilder("R").Score("c1", 0.9).Entry("quality", "m", "p").Write(_root, "one");
        var second = new LaneRunBuilder("R").Score("c1", 0.1, state: "failed").Entry("quality", "m", "p").Write(_root, "two");

        var store = AefRunStore.Open(_root);
        Assert.True(store.Has("R"));
        Assert.Equal(first, store.Find("R", LaneRunBuilder.RunHashOf(first))!.Directory);
        Assert.Equal(second, store.Find("R", LaneRunBuilder.RunHashOf(second))!.Directory);
        Assert.Null(store.Find("R", new string('0', 64)));
        Assert.False(store.Has("S"));
        Assert.Empty(store.WithRunId("S"));
    }

    [Fact]
    public void AnUnsealedRun_IsFoundByItsRecomputedRunHash_AndIsNeverIntact()
    {
        var dir = new LaneRunBuilder("U").Score("c1", 0.9).Entry("quality", "m", "p").Write(_root, seal: false);
        var store = AefRunStore.Open(_root);
        var run = store.Runs.Single();

        Assert.False(run.RunHash.Sealed);
        Assert.Same(run, store.Find("U", LaneRunBuilder.RunHashOf(dir)));
        Assert.False(run.Intact);
        Assert.Equal("2026-10-05T00:00:00Z", run.ClosedAt);
    }

    [Fact]
    public void AFolderHoldingARun_IsNotSearchedFurther_AndARunJsonThatNamesNoRunIsSkipped()
    {
        var dir = new LaneRunBuilder("Outer").Score("c1", 0.9).Entry("quality", "m", "p").Write(_root, "outer", seal: false);
        new LaneRunBuilder("Inner").Score("c1", 0.9).Write(dir, "ext/inner", seal: false);   // a file of the outer run
        Directory.CreateDirectory(Path.Combine(_root, "broken"));
        File.WriteAllText(Path.Combine(_root, "broken", "run.json"), """{"runId": "B", "runId": "C"}""");   // not I-JSON
        Directory.CreateDirectory(Path.Combine(_root, "numeric"));
        File.WriteAllText(Path.Combine(_root, "numeric", "run.json"), """{"runId": 7}""");

        var store = AefRunStore.Open(_root);
        Assert.Equal(["Outer"], store.Runs.Select(r => r.RunId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARedactedBlob_LeavesTheRunIntact_OnlyUnderAPolicyThatAuthorizesTheRedaction(bool withPolicy)
    {
        var vector = Path.Combine(AefCorpus.Conformance, "lane-vectors", "redacted-run");
        var policy = withPolicy ? TrustPolicy.Load(Path.Combine(vector, "policy.json")) : null;

        var store = AefRunStore.Open(Path.Combine(vector, "runs"), policy);
        var run = store.Runs.Single();

        Assert.True(run.RunHash.Sealed);
        Assert.Equal(withPolicy, run.Intact);
        Assert.Equal(withPolicy ? 1 : 0, run.Verification.Withheld);
        Assert.Equal("2026-10-02T14:06:23.004Z", run.ClosedAt);
    }

    [Fact]
    public void AFolderThatDoesNotExist_IsRefused() =>
        Assert.Throws<DirectoryNotFoundException>(() => AefRunStore.Open(Path.Combine(_root, "nowhere")));
}
