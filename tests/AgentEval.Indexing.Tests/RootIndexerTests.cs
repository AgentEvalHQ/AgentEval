// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Results.Ingestion;
using AgentEval.Results.Suites;

namespace AgentEval.Indexing.Tests;

public sealed class RootIndexerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ae-roots-index-" + Guid.NewGuid().ToString("N"));
    private string Sources => Path.Combine(_root, "sources");
    private string Nested => Path.Combine(Sources, "nested");
    private void Run(string directory, string id)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = "1.0", subject = new { kind = "agent", name = "fixture" },
            run = new { runId = id, timestamp = "2026-10-14T10:00:00Z", verdict = "PENDING", scenarioCount = 0 }
        }));
    }
    private SqliteEvidenceIndexWriter Writer() => new(new() { DatabasePath = Path.Combine(_root, "index.sqlite") }, new SqliteEvidenceIndexSchema());
    private static EvidenceRootIndexer Indexer(IEvidenceIndexWriter writer) => new(new EvidenceIndexRootPlanner(),
        new AefEvidenceIndexer(new AefRunDiscovery(), new AefIndexSourceReader(new SuiteIdentityReader()), writer),
        new LegacyEvidenceIndexer(new LegacyIndexSourceReader(), writer), writer);
    private EvidenceIndexRoot[] Roots => [new("parent", Sources, IndexedEvidenceKind.Legacy), new("child", Nested, IndexedEvidenceKind.Legacy)];

    [Fact]
    public void Prepare_ActualRootImport_RebuildsSourcesAndRefusesIncompleteCoverage()
    {
        Run(Path.Combine(Nested, "run"), "one");
        var target = new EvidenceIndexOptions { DatabasePath = Path.Combine(_root, "index.sqlite") };
        var service = new EvidenceIndexRebuildPreparation(new SqliteEvidenceIndexSchema());
        var prepared = service.Prepare(target, (writer, token) => Indexer(writer).Import(Roots, cancellationToken: token).Complete);
        using (var candidate = new SqliteEvidenceIndexWriter(new() { DatabasePath = prepared.DatabasePath }, new SqliteEvidenceIndexSchema()))
            Assert.NotNull(candidate.Find("parent", "nested/run"));
        var error = Assert.Throws<EvidenceIndexException>(() => service.Prepare(target,
            (writer, token) => Indexer(writer).Import(Roots, new() { MaxDepth = 0 }, cancellationToken: token).Complete));
        Assert.Equal("incomplete-rebuild", error.Code);
        Assert.Single(Directory.EnumerateDirectories(_root, ".ae-index-rebuild-*"));
        Assert.True(File.Exists(Path.Combine(Nested, "run/manifest.json")));
        Assert.False(File.Exists(target.DatabasePath));
    }

    [Fact]
    public void Import_MixedFormatsUnderSameDirectory_KeepsEvidenceClassesSeparate()
    {
        Run(Path.Combine(Nested, "legacy-run"), "legacy");
        string? fixture = null;
        for (var parent = new DirectoryInfo(AppContext.BaseDirectory); parent is not null; parent = parent.Parent)
        {
            var candidate = Path.Combine(parent.FullName, "contracts/aef/1/interop/examples/otel-aef/run");
            if (Directory.Exists(candidate)) { fixture = candidate; break; }
        }
        Assert.NotNull(fixture);
        foreach (var file in Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(Nested, "aef-run", Path.GetRelativePath(fixture, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
        }
        using var writer = Writer();
        var result = Indexer(writer).Import([new("aef", Sources, IndexedEvidenceKind.Aef),
            new("legacy", Sources, IndexedEvidenceKind.Legacy)]);
        Assert.True(result.Complete);
        Assert.Equal(2, result.Plan.ScanRoots.Length);
        Assert.Equal("aef", writer.Find("aef", "nested/aef-run")!.Kind);
        Assert.Equal("legacy", writer.Find("legacy", "nested/legacy-run")!.Kind);
        Assert.Null(writer.Find("aef", "nested/legacy-run"));
        Assert.Null(writer.Find("legacy", "nested/aef-run"));
    }

    [Fact]
    public void Import_AddOuterRoot_ReplacesCoveredNamespaceWithoutDeletingEvidence()
    {
        Run(Path.Combine(Nested, "run"), "one");
        using var writer = Writer();
        var indexer = Indexer(writer);
        Assert.True(indexer.Import([Roots[1]]).Complete);
        Assert.NotNull(writer.Find("child", "run"));
        var result = indexer.Import(Roots);
        Assert.True(result.Complete);
        Assert.Single(result.Plan.ScanRoots); Assert.Single(result.Plan.CoveredRoots);
        Assert.Equal(1, result.Roots.Single(r => r.RootId == "child").Removed);
        Assert.Null(writer.Find("child", "run"));
        Assert.NotNull(writer.Find("parent", "nested/run"));
        Assert.True(File.Exists(Path.Combine(Nested, "run/manifest.json")));
        Assert.All(indexer.Import(Enumerable.Reverse(Roots)).Roots, r => Assert.Equal(0, r.Changed));
    }

    [Fact]
    public void Import_FailedOwnerScan_RetainsCoveredRowsUntilSuccessfulRetry()
    {
        Run(Path.Combine(Nested, "run"), "one");
        using var writer = Writer();
        var indexer = Indexer(writer);
        indexer.Import([Roots[1]]);
        var result = indexer.Import(Roots, new() { MaxDepth = 0 });
        Assert.False(result.Complete);
        Assert.Contains(result.Issues, i => i.Code == "covering-scan-incomplete");
        Assert.NotNull(writer.Find("child", "run"));
        Assert.True(indexer.Import(Roots).Complete);
        Assert.Null(writer.Find("child", "run"));
    }

    [Theory]
    [InlineData(false, "covered-source-not-imported")]
    [InlineData(true, "covering-scan-incomplete")]
    public void Import_RunHiddenInsideNewMarker_DoesNotUseStaleReplacementAsProof(bool prune, string issue)
    {
        Run(Path.Combine(Nested, "run"), "one");
        using var writer = Writer();
        var indexer = Indexer(writer);
        indexer.Import([Roots[0]]); indexer.Import([Roots[1]]);
        Run(Sources, "outer-run");
        var result = indexer.Import(Roots, pruneMissing: prune);
        Assert.False(result.Complete);
        Assert.Contains(result.Issues, i => i.Code == issue);
        Assert.NotNull(writer.Find("child", "run"));
    }

    [Fact]
    public void Import_MissingCoveredSource_RequiresOptInBeforeRetiringOldRows()
    {
        var run = Path.Combine(Nested, "run"); Run(run, "one");
        using var writer = Writer();
        var indexer = Indexer(writer);
        indexer.Import([Roots[1]]);
        File.Delete(Path.Combine(run, "manifest.json"));
        Assert.False(indexer.Import(Roots).Complete);
        Assert.NotNull(writer.Find("child", "run"));
        Assert.True(indexer.Import(Roots, pruneMissing: true).Complete);
        Assert.Null(writer.Find("child", "run"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
