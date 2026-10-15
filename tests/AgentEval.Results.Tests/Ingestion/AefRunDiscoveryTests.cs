// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Results.Ingestion;

namespace AgentEval.Results.Tests.Ingestion;

public sealed class AefRunDiscoveryTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ae-discovery-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly AefRunDiscovery _discovery = new();
    private string Run(string relative)
    {
        var path = Directory.CreateDirectory(Path.Combine(_root, relative)).FullName;
        File.WriteAllText(Path.Combine(path, "run.json"), "intentionally not valid JSON");
        return path;
    }

    [Fact]
    public void Discover_EmptyRoot_IsCompleteWithNoCandidates()
    {
        var result = _discovery.Discover(_root);
        Assert.True(result.Complete);
        Assert.Empty(result.Candidates);
        Assert.Equal(0, result.InspectedEntries);
    }

    [Fact]
    public void Discover_NestedRuns_FindsSortedUnverifiedCandidatesWithoutChangingSource()
    {
        var z = Run("z/run");
        var a = Run("a/run");
        var hidden = Run(".hidden");
        var result = _discovery.Discover(_root);
        Assert.True(result.Complete);
        Assert.Equal(new[] { hidden, a, z }.Order(StringComparer.Ordinal), result.Candidates.Select(c => c.Directory));
        Assert.Equal("intentionally not valid JSON", File.ReadAllText(Path.Combine(a, "run.json")));
        Assert.Equal(1, Directory.EnumerateFileSystemEntries(a).Count());
    }

    [Fact]
    public void Discover_RunAtRoot_DoesNotDiscoverEmbeddedRuns()
    {
        Run("");
        Run("ext/embedded");
        var result = _discovery.Discover(_root, new() { MaxDepth = 0 });
        Assert.True(result.Complete);
        Assert.Equal(_root, Assert.Single(result.Candidates).Directory);
    }

    [Fact]
    public void Discover_MarkerIsDirectory_DoesNotTreatItAsRun()
    {
        Directory.CreateDirectory(Path.Combine(_root, "run.json"));
        Assert.Empty(_discovery.Discover(_root).Candidates);
    }

    [Fact]
    public void Discover_DepthLimit_ReportsOmittedSubtree()
    {
        Run("first/second");
        var result = _discovery.Discover(_root, new() { MaxDepth = 1 });
        Assert.False(result.Complete);
        Assert.Empty(result.Candidates);
        Assert.Contains(result.Issues, i => i.Path == "first/second" && i.Kind == AefDiscoveryIssueKind.DepthLimit);
    }

    [Fact]
    public void Discover_EntryLimit_BoundsInspectionAndReportsPartialScan()
    {
        for (var i = 0; i < 20; i++) File.WriteAllText(Path.Combine(_root, $"file{i}"), "");
        var result = _discovery.Discover(_root, new() { MaxEntries = 3 });
        Assert.Equal(3, result.InspectedEntries);
        Assert.False(result.Complete);
        Assert.Equal(AefDiscoveryIssueKind.EntryLimit, Assert.Single(result.Issues).Kind);
    }

    [Fact]
    public void Discover_ExactBudgets_DoesNotReportTruncation()
    {
        Run("");
        var result = _discovery.Discover(_root, new() { MaxEntries = 1, MaxRuns = 1 });
        Assert.True(result.Complete);
        Assert.Single(result.Candidates);
    }

    [Fact]
    public void Discover_RunLimit_ReturnsBoundedCandidatesAndExplicitOmission()
    {
        Run("a"); Run("b");
        var result = _discovery.Discover(_root, new() { MaxRuns = 1 });
        Assert.Single(result.Candidates);
        Assert.False(result.Complete);
        Assert.Equal(AefDiscoveryIssueKind.RunLimit, Assert.Single(result.Issues).Kind);
    }

    [Fact]
    public void Discover_PreCancelled_ThrowsWithoutSuccessfulResult()
    {
        Assert.Throws<OperationCanceledException>(() => _discovery.Discover(_root, cancellationToken: new(true)));
    }

    [Theory]
    [InlineData(-1, 1, 1)]
    [InlineData(65, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1_000_001, 1)]
    [InlineData(1, 1, 0)]
    [InlineData(1, 1, 100_001)]
    public void Discover_InvalidOptions_Refuses(int depth, int entries, int runs)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _discovery.Discover(_root,
            new() { MaxDepth = depth, MaxEntries = entries, MaxRuns = runs }));
    }

    [Fact]
    public void Discover_RelativeOrMissingRoot_RefusesRatherThanReportingCompleteEmptyScan()
    {
        Assert.Throws<ArgumentException>(() => _discovery.Discover("relative"));
        Assert.ThrowsAny<IOException>(() => _discovery.Discover(Path.Combine(_root, "missing")));
    }

    [Fact]
    public void Discover_LinkedRootOrAncestor_Refuses()
    {
        if (OperatingSystem.IsWindows()) return;
        var actual = Run("actual/nested");
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, Path.GetDirectoryName(actual)!);
        Assert.Throws<IOException>(() => _discovery.Discover(link));
        Assert.Throws<IOException>(() => _discovery.Discover(Path.Combine(link, "nested")));
    }

    [Fact]
    public void Discover_LinkedMarkerAndLoop_SkipsBothWithoutFollowingThem()
    {
        if (OperatingSystem.IsWindows()) return;
        var source = Run("source");
        var tree = Directory.CreateDirectory(Path.Combine(_root, "tree")).FullName;
        File.CreateSymbolicLink(Path.Combine(tree, "run.json"), Path.Combine(source, "run.json"));
        Directory.CreateSymbolicLink(Path.Combine(tree, "loop"), tree);
        var result = _discovery.Discover(tree);
        Assert.Empty(result.Candidates);
        Assert.False(result.Complete);
        Assert.Equal(2, result.Issues.Count);
        Assert.All(result.Issues, issue => Assert.Equal(AefDiscoveryIssueKind.LinkSkipped, issue.Kind));
    }

    [Fact]
    public void Discover_SocketMarker_DoesNotOpenOrTreatItAsRun()
    {
        if (OperatingSystem.IsWindows()) return;
        using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix,
            System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
        socket.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(Path.Combine(_root, "run.json")));
        var result = _discovery.Discover(_root);
        Assert.Empty(result.Candidates);
        Assert.Equal(AefDiscoveryIssueKind.SpecialEntrySkipped, Assert.Single(result.Issues).Kind);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
