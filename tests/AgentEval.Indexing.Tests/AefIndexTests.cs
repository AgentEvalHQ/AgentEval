// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.Results.Ingestion;
using AgentEval.Results.Suites;

namespace AgentEval.Indexing.Tests;

public sealed class AefIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ae-aef-index-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_root, "index.sqlite");
    private string Sources => Path.Combine(_root, "sources");
    private static string Fixture()
    {
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent)
        {
            var fixture = Path.Combine(path.FullName, "contracts/aef/1/interop/examples/otel-aef/run");
            if (Directory.Exists(fixture)) return fixture;
        }
        throw new DirectoryNotFoundException("AEF interoperability fixture not found.");
    }
    private string Copy(string name = "run")
    {
        var target = Path.Combine(Sources, name);
        foreach (var source in Directory.EnumerateFiles(Fixture(), "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(Fixture(), source));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination);
        }
        return target;
    }
    private SqliteEvidenceIndexWriter Writer() => new(new() { DatabasePath = Database }, new SqliteEvidenceIndexSchema());
    private static AefIndexSourceReader Reader() => new(new SuiteIdentityReader());

    [Fact]
    public void Read_SealedInteropFixture_ProjectsReportedStatesWithoutInventingRunVerdict()
    {
        var source = Reader().Read("root", "run", Copy());
        Assert.Equal(IndexedVerification.Intact, source.Verification);
        Assert.Equal("otel-import-0001", source.Run!.RunId);
        Assert.Equal("agent:support/support-triage", source.Run.SubjectId);
        Assert.NotNull(source.Run.RunHash); Assert.Null(source.Run.Outcome);
        Assert.Equal("unknown", source.Run.Completeness); Assert.Null(source.Run.SuiteDefinitionDigest);
        Assert.Equal(8, source.Results.Length);
        Assert.Contains(source.Results, r => r.CaseId == "case-17" && r.Outcome == "failed");
        var metric = Assert.Single(source.Metrics.Where(m => m.MetricId == "helpfulness"));
        Assert.Equal(0.1, metric.Score); Assert.Null(metric.Passed);
        Assert.StartsWith("aef-base-reported:", metric.Provenance);
    }

    [Fact]
    public void Read_UnsealedFixture_IsUnverifiedAndHasNoSealedHash()
    {
        var directory = Copy(); File.Delete(Path.Combine(directory, "seal.json"));
        var source = Reader().Read("root", "run", directory);
        Assert.Equal(IndexedVerification.Unverified, source.Verification);
        Assert.Null(source.Run!.RunHash); Assert.Equal(8, source.Results.Length);
    }

    [Fact]
    public void Read_SourceChangesAfterSnapshot_ProjectsOnlyVerifiedSnapshotAndRemovesScratch()
    {
        var directory = Copy();
        var hook = new IdentityHook(snapshot =>
        {
            Assert.NotEqual(directory, snapshot);
            File.WriteAllText(Path.Combine(directory, "results.ndjson"), "tampered after capture");
        });
        var source = new AefIndexSourceReader(hook).Read("root", "run", directory);
        Assert.Equal(IndexedVerification.Intact, source.Verification);
        Assert.Equal(8, source.Results.Length);
        Assert.False(Directory.Exists(hook.Snapshot));
        Assert.Equal("invalid-aef", Assert.Throws<EvidenceIndexException>(() => Reader().Read("root", "run", directory)).Code);
    }

    [Fact]
    public void Read_ByteLimitAndCancellation_RefuseBeforeProjection()
    {
        var directory = Copy();
        var reader = new AefIndexSourceReader(new SuiteIdentityReader(), new() { MaxBytes = 1, MaxFileBytes = 1 });
        Assert.Equal("snapshot-limit", Assert.Throws<EvidenceIndexException>(() => reader.Read("root", "run", directory)).Code);
        Assert.Throws<OperationCanceledException>(() => Reader().Read("root", "run", directory, new(true)));
        Assert.True(File.Exists(Path.Combine(directory, "seal.json")));
    }

    [Fact]
    public void Import_InitialUnchangedChangedAndInvalidSources_UpdatesAtomicallyWithHonestCoverage()
    {
        var directory = Copy();
        using var writer = Writer();
        var indexer = new AefEvidenceIndexer(new AefRunDiscovery(), Reader(), writer);
        var first = indexer.Import("root", Sources);
        Assert.True(first.Complete); Assert.Equal(1, first.Changed);
        Assert.Equal(1, indexer.Import("root", Sources).Unchanged);
        var checkpoint = writer.Find("root", "run")!;
        File.Delete(Path.Combine(directory, "seal.json"));
        var headerPath = Path.Combine(directory, "run.json");
        var header = JsonNode.Parse(File.ReadAllText(headerPath))!;
        header["producer"]!["version"] = "changed"; File.WriteAllText(headerPath, header.ToJsonString());
        var changed = indexer.Import("root", Sources);
        Assert.True(changed.Complete); Assert.Equal(1, changed.Changed);
        Assert.NotEqual(checkpoint.Fingerprint, writer.Find("root", "run")!.Fingerprint);
        checkpoint = writer.Find("root", "run")!;
        File.WriteAllText(Path.Combine(directory, "results.ndjson"), "invalid");
        var invalid = indexer.Import("root", Sources);
        Assert.False(invalid.Complete); Assert.Equal("invalid-aef", Assert.Single(invalid.Issues).Code);
        Assert.Equal(checkpoint, writer.Find("root", "run"));
    }

    [Fact]
    public void Import_DiscoveryLimit_DoesNotPruneUnvisitedSources()
    {
        Copy("first"); Copy("second");
        using var writer = Writer();
        var indexer = new AefEvidenceIndexer(new AefRunDiscovery(), Reader(), writer);
        Assert.Equal(2, indexer.Import("root", Sources).Changed);
        var limited = indexer.Import("root", Sources, new() { MaxRuns = 1 }, pruneMissing: true);
        Assert.False(limited.Complete);
        Assert.NotNull(writer.Find("root", "first")); Assert.NotNull(writer.Find("root", "second"));
    }

    [Fact]
    public void Import_PruneMissing_RequiresOptInAndSuccessfulCompletePass()
    {
        var first = Copy("first"); var second = Copy("second");
        using var writer = Writer();
        var indexer = new AefEvidenceIndexer(new AefRunDiscovery(), Reader(), writer);
        Assert.Equal(2, indexer.Import("root", Sources).Changed);
        Directory.Delete(first, true);
        Assert.Equal(0, indexer.Import("root", Sources).Removed);
        Assert.NotNull(writer.Find("root", "first"));
        var resultPath = Path.Combine(second, "results.ndjson"); var bytes = File.ReadAllBytes(resultPath);
        File.WriteAllText(resultPath, "invalid");
        Assert.False(indexer.Import("root", Sources, pruneMissing: true).Complete);
        Assert.NotNull(writer.Find("root", "first"));
        File.WriteAllBytes(resultPath, bytes);
        var reconciled = indexer.Import("root", Sources, pruneMissing: true);
        Assert.True(reconciled.Complete); Assert.Equal(1, reconciled.Removed);
        Assert.Null(writer.Find("root", "first")); Assert.NotNull(writer.Find("root", "second"));
    }

    private sealed class IdentityHook(Action<string> action) : ISuiteIdentityReader
    {
        public string? Snapshot { get; private set; }
        public SuiteIdentity Read(string directory, string? expectedRunHash = null)
        {
            Snapshot = directory;
            var result = new SuiteIdentityReader().Read(directory, expectedRunHash);
            action(directory);
            return result;
        }
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
