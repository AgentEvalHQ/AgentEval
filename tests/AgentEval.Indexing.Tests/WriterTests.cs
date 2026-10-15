// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using Microsoft.Data.Sqlite;

namespace AgentEval.Indexing.Tests;

public sealed class WriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ae-index-writer-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_root, "index.sqlite");
    private SqliteEvidenceIndexWriter Open(EvidenceIndexWriteLimits? limits = null, long bytes = 512L << 20) =>
        new(new() { DatabasePath = Database, MaxDatabaseBytes = bytes }, new SqliteEvidenceIndexSchema(), limits);
    private static EvidenceIndexSource Source(string path = "run", string outcome = "passed", char digest = 'a') =>
        new("root", path, new string(digest, 64), IndexedEvidenceKind.Aef, IndexedVerification.Intact,
            new("run-id", new string('b', 64), "subject", "suite", null, null, outcome, "unknown"),
            [new("result", "case", 0, outcome)], [new("result", "metric", 0, 0.5, null, true, "reported")], null);
    private object? Scalar(string sql)
    {
        using var connection = SqliteEvidenceIndexSchema.Open(Database, 1);
        using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar();
    }

    [Fact]
    public void Upsert_UnchangedThenChangedProjection_AdvancesOnlyForCommittedChanges()
    {
        using var writer = Open();
        var first = writer.Upsert(Source());
        Assert.True(first.Changed); Assert.Equal(1, first.Generation);
        Assert.False(writer.Upsert(Source()).Changed);
        // Same source digest but changed projection must not be skipped.
        var second = writer.Upsert(Source(outcome: "failed"));
        Assert.True(second.Changed); Assert.Equal(2, second.Generation);
        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
        Assert.Equal("failed", Scalar("SELECT outcome FROM evaluation_results"));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM sources"));
        Assert.Equal(second.Fingerprint, writer.Find("root", "run")!.Fingerprint);
    }

    [Fact]
    public void Upsert_ConstraintFailsAfterDeletion_RollsBackPreviousRowsAndGeneration()
    {
        using var writer = Open();
        var initial = writer.Upsert(Source());
        var invalid = Source(outcome: "failed", digest: 'c') with { Metrics = [new("missing-result", "metric", 0, 0.1, null, false, "reported")] };
        Assert.Equal("invalid-projection", Assert.Throws<EvidenceIndexException>(() => writer.Upsert(invalid)).Code);
        Assert.Equal(initial.Fingerprint, writer.Find("root", "run")!.Fingerprint);
        Assert.Equal("passed", Scalar("SELECT outcome FROM evaluation_results"));
        Assert.Equal(1L, Scalar("SELECT generation FROM index_metadata"));
        Assert.Equal(0.5, Scalar("SELECT score FROM metric_values"));
    }

    [Fact]
    public void Upsert_ReorderedResultsAndMetrics_HasSameCheckpoint()
    {
        using var writer = Open();
        var first = Source() with { Results = [new("b", null, null, "passed"), new("a", null, null, "failed")],
            Metrics = [new("b", "metric", 0, 1, null, true, "reported"), new("a", "metric", 0, 0, null, false, "reported")] };
        var receipt = writer.Upsert(first);
        var retry = writer.Upsert(first with { Results = [.. first.Results.Reverse()], Metrics = [.. first.Metrics.Reverse()] });
        Assert.False(retry.Changed); Assert.Equal(receipt.Fingerprint, retry.Fingerprint);
    }

    [Fact]
    public void Remove_StaleCheckpoint_RefusesAndValidCheckpointCascades()
    {
        using var writer = Open();
        var first = writer.Upsert(Source()); var second = writer.Upsert(Source(outcome: "failed"));
        Assert.Equal("stale-checkpoint", Assert.Throws<EvidenceIndexException>(() => writer.Remove("root", "run", first.Fingerprint)).Code);
        var removed = writer.Remove("root", "run", second.Fingerprint);
        Assert.True(removed.Changed); Assert.Equal(3, removed.Generation);
        Assert.Null(writer.Find("root", "run"));
        Assert.Equal(0L, Scalar("SELECT (SELECT count(*) FROM evaluation_runs)+(SELECT count(*) FROM evaluation_results)+(SELECT count(*) FROM metric_values)"));
        Assert.False(writer.Remove("root", "run", second.Fingerprint).Changed);
    }

    [Fact]
    public void Upsert_SourceCapacity_AllowsRetryAndReplacementButRefusesNewSource()
    {
        using var writer = Open(new() { MaxSources = 1 });
        writer.Upsert(Source());
        Assert.False(writer.Upsert(Source()).Changed);
        Assert.True(writer.Upsert(Source(outcome: "failed")).Changed);
        Assert.Equal("source-limit", Assert.Throws<EvidenceIndexException>(() => writer.Upsert(Source("other"))).Code);
        Assert.Equal(1L, Scalar("SELECT count(*) FROM sources"));
    }

    [Theory]
    [InlineData(3, 100, "source-row-limit")]
    [InlineData(100, 3, "total-row-limit")]
    public void Upsert_RowBudgets_RefuseWithoutPartialSource(int perSource, int total, string code)
    {
        using var writer = Open(new() { MaxRowsPerSource = perSource, MaxTotalRows = total });
        Assert.Equal(code, Assert.Throws<EvidenceIndexException>(() => writer.Upsert(Source())).Code);
        Assert.Null(writer.Find("root", "run"));
        Assert.Equal(0L, Scalar("SELECT generation FROM index_metadata"));
    }

    [Fact]
    public void Upsert_TextAndDatabaseBudgets_LeaveOldEvidenceIntact()
    {
        using (var writer = Open(new() { MaxTextBytesPerSource = 2000 }))
        {
            var original = writer.Upsert(Source());
            Assert.Equal("projection-byte-limit", Assert.Throws<EvidenceIndexException>(() => writer.Upsert(Source(outcome: new string('x', 2001)))).Code);
            Assert.Equal(original.Fingerprint, writer.Find("root", "run")!.Fingerprint);
        }
        var maximum = new FileInfo(Database).Length;
        using var bounded = Open(bytes: maximum);
        Assert.Equal("size-limit", Assert.Throws<EvidenceIndexException>(() => bounded.Upsert(Source(outcome: new string('x', 40000)))).Code);
        Assert.Equal("passed", Scalar("SELECT outcome FROM evaluation_runs"));
    }

    [Fact]
    public void Upsert_Reopen_IsIdempotentAndPreservesInstance()
    {
        EvidenceIndexMutation first;
        using (var writer = Open()) first = writer.Upsert(Source());
        using var reopened = Open();
        Assert.Equal(first with { Changed = false }, reopened.Upsert(Source()));
    }

    [Fact]
    public void Operations_CancelledOrUnsafeIdentity_RefuseWithoutChange()
    {
        using var writer = Open();
        Assert.Throws<OperationCanceledException>(() => writer.Upsert(Source(), new(true)));
        Assert.Throws<EvidenceIndexException>(() => writer.Upsert(Source("../outside")));
        Assert.Throws<EvidenceIndexException>(() => writer.Upsert(Source() with { ContentDigest = "invalid" }));
        Assert.Equal(0L, Scalar("SELECT generation FROM index_metadata"));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
