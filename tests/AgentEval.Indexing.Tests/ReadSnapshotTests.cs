// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Indexing.Tests;

public sealed class ReadSnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ae-read-snapshot-" + Guid.NewGuid().ToString("N"));
    private EvidenceIndexOptions Options => new() { DatabasePath = Path.Combine(_root, "index.sqlite") };
    private static EvidenceIndexSource Source(string outcome) => new("root", "run", new string('a', 64),
        IndexedEvidenceKind.Legacy, IndexedVerification.Unverified,
        new("run", null, null, null, null, null, outcome, "unknown"), [new("result", "case", null, outcome)], [], null);

    public ReadSnapshotTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Open_WalWriterCommitsAfterValidation_KeepsValidatedGenerationAndRowsTogether()
    {
        var schema = new SqliteEvidenceIndexSchema(); schema.EnsureCurrent(Options);
        using (var setup = SqliteEvidenceIndexSchema.Open(Options.DatabasePath, 2))
            SqliteEvidenceIndexSchema.Execute(setup, null, "PRAGMA journal_mode=WAL");
        using var writer = new SqliteEvidenceIndexWriter(Options, schema);
        var initial = writer.Upsert(Source("PASS"));
        using var snapshot = EvidenceIndexReadSnapshot.Open(Options, default);
        var changed = writer.Upsert(Source("FAIL"));
        Assert.True(changed.Generation > initial.Generation);
        Assert.Equal(initial.Generation, snapshot.Generation);
        using var command = snapshot.Connection.CreateCommand(); command.Transaction = snapshot.Transaction;
        command.CommandText = "SELECT outcome FROM evaluation_results";
        Assert.Equal("PASS", command.ExecuteScalar());
        var latest = new SqliteEvidenceDetailReader(Options).ReadResults(new(initial.SourceId));
        Assert.Equal(changed.Generation, latest.Generation);
        Assert.Equal("FAIL", Assert.Single(latest.Items).Outcome);
    }

    [Fact]
    public void Read_ExternalBrokenReferencesWithoutGenerationChange_StillRefuses()
    {
        using (var writer = new SqliteEvidenceIndexWriter(Options, new SqliteEvidenceIndexSchema())) writer.Upsert(Source("PASS"));
        var reader = new SqliteEvidenceSourceReader(Options); var before = reader.Read(new());
        using (var corrupt = SqliteEvidenceIndexSchema.Open(Options.DatabasePath, 2))
            SqliteEvidenceIndexSchema.Execute(corrupt, null, "PRAGMA foreign_keys=OFF; DELETE FROM sources;");
        Assert.Equal("broken-references", Assert.Throws<EvidenceIndexException>(() => reader.Read(new())).Code);
        using var check = SqliteEvidenceIndexSchema.Open(Options.DatabasePath, 2, readOnly: true);
        using var command = check.CreateCommand(); command.CommandText = "SELECT generation FROM index_metadata";
        Assert.Equal(before.Generation, (long)command.ExecuteScalar()!);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
