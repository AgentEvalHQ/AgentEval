// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Indexing.Tests;

public sealed class RootPlannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ae-root-plan-" + Guid.NewGuid().ToString("N"));
    private EvidenceIndexRoot Root(string id, string path, IndexedEvidenceKind kind = IndexedEvidenceKind.Aef)
    {
        var directory = Path.Combine(_root, path);
        Directory.CreateDirectory(directory);
        return new(id, directory, kind);
    }

    [Fact]
    public void Plan_OverlappingRoots_ReportsOutermostOwnershipIndependentOfInputOrder()
    {
        EvidenceIndexRoot[] roots = [Root("child", "runs/nested"), Root("parent", "runs"),
            Root("sibling", "runs-other"), Root("alias", "runs/./"), Root("legacy", "runs/nested", IndexedEvidenceKind.Legacy)];
        var planner = new EvidenceIndexRootPlanner();
        var plan = planner.Plan(roots);
        var reversed = planner.Plan(Enumerable.Reverse(roots));
        Assert.Equal(new[] { "alias", "legacy", "sibling" }, plan.ScanRoots.Select(r => r.RootId));
        Assert.Equal(new[] { "child", "parent" }, plan.CoveredRoots.Select(r => r.Root.RootId));
        Assert.All(plan.CoveredRoots, r => Assert.Equal("alias", r.CoveringRootId));
        Assert.Equal(plan.ScanRoots.ToArray(), reversed.ScanRoots.ToArray());
        Assert.Equal(plan.CoveredRoots.ToArray(), reversed.CoveredRoots.ToArray());
    }

    [Fact]
    public void Plan_DuplicateNamespace_RefusesEvenAcrossFormats()
    {
        var error = Assert.Throws<EvidenceIndexException>(() => new EvidenceIndexRootPlanner().Plan(
            [Root("same", "one"), Root("same", "two", IndexedEvidenceKind.Legacy)]));
        Assert.Equal("duplicate-root-id", error.Code);
    }

    [Fact]
    public void Plan_UnboundedEnumeration_StopsAtOneLookahead()
    {
        var count = 0;
        var root = Root("root", "runs");
        IEnumerable<EvidenceIndexRoot> Roots()
        {
            while (true) { count++; yield return root with { RootId = count.ToString() }; }
        }
        Assert.Equal("root-limit", Assert.Throws<EvidenceIndexException>(() => new EvidenceIndexRootPlanner().Plan(Roots(), 3)).Code);
        Assert.Equal(4, count);
    }

    [Fact]
    public void Plan_MissingOrRelativeDirectory_Refuses()
    {
        var planner = new EvidenceIndexRootPlanner();
        Assert.Equal("unsafe-source-path", Assert.Throws<EvidenceIndexException>(() => planner.Plan(
            [new("missing", Path.Combine(_root, "missing"), IndexedEvidenceKind.Aef)])).Code);
        Assert.Equal("invalid-source-path", Assert.Throws<EvidenceIndexException>(() => planner.Plan(
            [new("relative", "relative", IndexedEvidenceKind.Aef)])).Code);
    }

    [Fact]
    public void Plan_Cancelled_ThrowsBeforeEnumeration()
    {
        Assert.Throws<OperationCanceledException>(() => new EvidenceIndexRootPlanner().Plan([], cancellationToken: new(true)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
