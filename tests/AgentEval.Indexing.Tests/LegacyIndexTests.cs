// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Output;

namespace AgentEval.Indexing.Tests;

public sealed class LegacyIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ae-legacy-index-" + Guid.NewGuid().ToString("N"));
    private void Write(string path, object value)
    {
        var target = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, JsonSerializer.Serialize(value));
    }
    private void Fixture(bool pending = false)
    {
        Write("manifest.json", new { schemaVersion = "1.0", subject = new { kind = "agent", name = "example" },
            run = new { runId = "legacy-run", timestamp = "2026-10-14T10:00:00Z", scenarioCount = pending ? 0 : 1, verdict = pending ? "PENDING" : "PASS" } });
        if (pending) return;
        Write("summary.json", new { schemaVersion = "1.0", runId = "legacy-run", verdict = "PASS", stats = new { total = 1 }, metrics = new { accuracy = 0.75 } });
        Write("scenarios/case.json", new { id = "case", input = "secret prompt", output = "secret response", passed = true, score = 0.75, metrics = new { accuracy = 0.75 } });
    }
    private EvidenceIndexSource Read(LegacyIndexReadLimits? limits = null) => new LegacyIndexSourceReader(limits).Read("legacy", "run", _root);

    [Fact]
    public async Task Read_ActualOutputStoreLifecycle_ProjectsPendingThenCompletedRun()
    {
        Write("solution.json", new { schemaVersion = "1.0", id = Guid.NewGuid(), name = "IndexFixture" });
        var store = new FileSystemOutputStore(_root);
        var subject = new SubjectIdentity(SubjectKind.Agent, "legacy-example");
        var manifest = await store.StartRunAsync(subject, new RunContext("fixture", "/fixture", "xunit", null, null, "eval"));
        var directory = new FileSystemLayout(_root).RunDir(subject, manifest.Run.RunId);
        var reader = new LegacyIndexSourceReader();
        Assert.Equal("partial", reader.Read("legacy", "run", directory).Run!.Completeness);
        await store.WriteScenarioResultAsync(manifest.Run.RunId, new ScenarioResult("scenario-01", "Scenario", "input", "output", true, 0.8,
            new Dictionary<string, double> { ["accuracy"] = 0.8 }, [], TimeSpan.FromSeconds(1), 0));
        await store.CompleteRunAsync(manifest, new RunSummary("1.0", manifest.Run.RunId, "PASS", new RunStats(1, 1, 0, 0),
            new Dictionary<string, double> { ["accuracy"] = 0.8 }));
        var source = reader.Read("legacy", "run", directory);
        Assert.Equal(manifest.Run.RunId, source.Run!.RunId);
        Assert.Equal("agent:legacy-example", source.Run.SubjectId);
        Assert.Equal("PASS", source.Run.Outcome);
        Assert.Equal("unknown", source.Run.Completeness);
        Assert.Equal(IndexedVerification.Unverified, source.Verification);
        Assert.Null(source.Run.RunHash);
        Assert.Equal(0.8, source.Metrics.Single(m => m.Provenance == "legacy-scenario-score").Score);
        using var writer = new SqliteEvidenceIndexWriter(new() { DatabasePath = Path.Combine(_root, "derived.sqlite") }, new SqliteEvidenceIndexSchema());
        Assert.True(writer.Upsert(source).Changed);
        Assert.False(writer.Upsert(reader.Read("legacy", "run", directory)).Changed);
    }

    [Fact]
    public void Read_LegacyRun_PreservesProducerClaimsWithoutTrustOrCompatibility()
    {
        Fixture();
        var source = Read();
        Assert.Equal(IndexedEvidenceKind.Legacy, source.Kind);
        Assert.Equal(IndexedVerification.Unverified, source.Verification);
        Assert.Equal("PASS", source.Run!.Outcome);
        Assert.Equal("unknown", source.Run.Completeness);
        Assert.Null(source.Run.RunHash); Assert.Null(source.Run.SuiteId); Assert.Null(source.Run.SuiteDefinitionDigest);
        Assert.Equal(2, source.Results.Length);
        Assert.Null(source.Results.Single(r => r.ResultId == "legacy:summary").CaseId);
        Assert.Equal("case", source.Results.Single(r => r.ResultId != "legacy:summary").CaseId);
        Assert.Equal(3, source.Metrics.Length);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(source));
        using var writer = new SqliteEvidenceIndexWriter(new() { DatabasePath = Path.Combine(_root, "derived.sqlite") }, new SqliteEvidenceIndexSchema());
        Assert.True(writer.Upsert(source).Changed);
        Assert.False(writer.Upsert(Read()).Changed);
        Write("scenarios/case.json", new { id = "case", passed = false, score = 0.2, metrics = new { accuracy = 0.2 } });
        var changed = Read();
        Assert.NotEqual(source.ContentDigest, changed.ContentDigest);
        Assert.True(writer.Upsert(changed).Changed);
        Assert.Equal("FAIL", changed.Results.Single(r => r.CaseId == "case").Outcome);
    }

    [Fact]
    public void Read_PendingOrMissingScenarios_RemainsPartial()
    {
        Fixture(pending: true);
        Assert.Equal("partial", Read().Run!.Completeness);
        Assert.Empty(Read().Results);
        Fixture();
        File.Delete(Path.Combine(_root, "scenarios/case.json"));
        Assert.Equal("partial", Read().Run!.Completeness);
    }

    [Fact]
    public void Read_MismatchedSummary_Refuses()
    {
        Fixture();
        Write("summary.json", new { schemaVersion = "1.0", runId = "other", verdict = "PASS" });
        Assert.Equal("inconsistent-legacy-summary", Assert.Throws<EvidenceIndexException>(() => Read()).Code);
    }

    [Fact]
    public void Read_DuplicatePropertiesOrScenarioIds_Refuses()
    {
        Fixture();
        File.Copy(Path.Combine(_root, "scenarios/case.json"), Path.Combine(_root, "scenarios/duplicate.json"));
        Assert.Equal("duplicate-legacy-scenario", Assert.Throws<EvidenceIndexException>(() => Read()).Code);
        File.Delete(Path.Combine(_root, "scenarios/duplicate.json"));
        File.WriteAllText(Path.Combine(_root, "scenarios/case.json"), "{\"id\":\"a\",\"id\":\"b\"}");
        Assert.Equal("ambiguous-legacy-json", Assert.Throws<EvidenceIndexException>(() => Read()).Code);
    }

    [Fact]
    public void Read_ByteOrEntryBudget_RefusesWithoutReturningPartialProjection()
    {
        Fixture();
        Assert.Equal("legacy-byte-limit", Assert.Throws<EvidenceIndexException>(() => Read(new() { MaxFileBytes = 8 })).Code);
        Assert.Equal("legacy-row-limit", Assert.Throws<EvidenceIndexException>(() => Read(new() { MaxProjectedRows = 4 })).Code);
        Assert.Equal(3, Read(new() { MaxProjectedRows = 5 }).Metrics.Length);
        Write("scenarios/extra.txt", new { ignored = true });
        Assert.Equal("legacy-entry-limit", Assert.Throws<EvidenceIndexException>(() => Read(new() { MaxScenarioEntries = 1 })).Code);
    }

    [Fact]
    public void Read_NonfiniteScoreOrUnknownVersion_Refuses()
    {
        Fixture();
        File.WriteAllText(Path.Combine(_root, "scenarios/case.json"), "{\"id\":\"a\",\"passed\":true,\"score\":1e999,\"metrics\":{}}");
        Assert.Equal("invalid-legacy", Assert.Throws<EvidenceIndexException>(() => Read()).Code);
        Write("manifest.json", new { schemaVersion = "2.0" });
        Assert.Equal("unsupported-legacy-version", Assert.Throws<EvidenceIndexException>(() => Read()).Code);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
