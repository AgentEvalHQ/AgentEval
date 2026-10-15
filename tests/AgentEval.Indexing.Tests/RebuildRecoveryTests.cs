// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;

namespace AgentEval.Indexing.Tests;

public sealed class RebuildRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ae-index-recovery-test-" + Guid.NewGuid().ToString("N"));
    private EvidenceIndexOptions Options => new() { DatabasePath = Path.Combine(_root, "active.sqlite") };
    public RebuildRecoveryTests() => Directory.CreateDirectory(_root);
    private PreparedEvidenceIndex Prepare() => new EvidenceIndexRebuildPreparation(new SqliteEvidenceIndexSchema()).Prepare(Options, (_, _) => true);

    [Fact]
    public void Inspect_ReadyCandidateAfterReopen_VerifiesReceiptDigestAndSchema()
    {
        var prepared = Prepare();
        var recovered = new SqliteEvidenceIndexRebuildRecovery().Inspect(Options, prepared.Directory);
        Assert.True(recovered.Ready); Assert.Equal(prepared, recovered.Candidate);
        Assert.False(File.Exists(Options.DatabasePath));
    }

    [Fact]
    public void Inspect_ChangedDatabase_RefusesAndDiscardRemovesOnlyCandidate()
    {
        File.WriteAllText(Options.DatabasePath, "original");
        var prepared = Prepare();
        File.WriteAllText(prepared.DatabasePath, "corrupt candidate");
        var recovery = new SqliteEvidenceIndexRebuildRecovery();
        Assert.Equal("rebuild-digest-mismatch", Assert.Throws<EvidenceIndexException>(() => recovery.Inspect(Options, prepared.Directory)).Code);
        recovery.Discard(Options, prepared.Directory);
        Assert.False(Directory.Exists(prepared.Directory));
        Assert.Equal("original", File.ReadAllText(Options.DatabasePath));
    }

    [Fact]
    public void Inspect_InterruptedBuildWithoutReadyReceipt_IsNotPublishableAndCanBeDiscarded()
    {
        var prepared = Prepare();
        File.Delete(Path.Combine(prepared.Directory, "ready.json"));
        File.WriteAllText(prepared.DatabasePath, "incomplete write");
        File.WriteAllText(prepared.DatabasePath + "-journal", "interrupted journal");
        var recovery = new SqliteEvidenceIndexRebuildRecovery();
        Assert.False(recovery.Inspect(Options, prepared.Directory).Ready);
        recovery.Discard(Options, prepared.Directory);
        Assert.False(Directory.Exists(prepared.Directory));
    }

    [Fact]
    public void Inspect_ReadyCandidateWithJournal_RefusesWithoutDeletingJournal()
    {
        var prepared = Prepare();
        File.WriteAllText(prepared.DatabasePath + "-journal", "journal");
        Assert.Equal("rebuild-sidecars-present", Assert.Throws<EvidenceIndexException>(() =>
            new SqliteEvidenceIndexRebuildRecovery().Inspect(Options, prepared.Directory)).Code);
        Assert.True(File.Exists(prepared.DatabasePath + "-journal"));
    }

    [Fact]
    public void Discard_UnknownEntryOrWrongOwner_RefusesWithoutRemovingAnything()
    {
        var prepared = Prepare();
        var extra = Path.Combine(prepared.Directory, "unrelated.txt"); File.WriteAllText(extra, "keep");
        var recovery = new SqliteEvidenceIndexRebuildRecovery();
        Assert.Equal("unexpected-rebuild-entry", Assert.Throws<EvidenceIndexException>(() => recovery.Discard(Options, prepared.Directory)).Code);
        Assert.True(File.Exists(prepared.DatabasePath)); Assert.True(File.Exists(extra));
        File.Delete(extra);
        File.WriteAllText(Path.Combine(prepared.Directory, "owner.json"), JsonSerializer.Serialize(new { Version = 1, CandidateId = "wrong", TargetDatabasePath = Options.DatabasePath }));
        Assert.Equal("unowned-rebuild-directory", Assert.Throws<EvidenceIndexException>(() => recovery.Discard(Options, prepared.Directory)).Code);
        Assert.True(File.Exists(prepared.DatabasePath));
    }

    [Fact]
    public void Inspect_ActiveCandidateWriter_RefusesUntilWriterCloses()
    {
        var prepared = Prepare();
        var recovery = new SqliteEvidenceIndexRebuildRecovery();
        using (var writer = new SqliteEvidenceIndexWriter(new() { DatabasePath = prepared.DatabasePath }, new SqliteEvidenceIndexSchema()))
            Assert.ThrowsAny<IOException>(() => recovery.Inspect(Options, prepared.Directory));
        Assert.True(recovery.Inspect(Options, prepared.Directory).Ready);
    }

    [Fact]
    public void Inspect_EmptyCandidateWithRewrittenDigest_DoesNotInitializeDatabase()
    {
        var prepared = Prepare();
        File.WriteAllBytes(prepared.DatabasePath, []);
        using (var stream = File.OpenRead(prepared.DatabasePath))
            prepared = prepared with { ContentDigest = EvidenceIndexRebuildPreparation.Hash(stream, Options.MaxDatabaseBytes, default) };
        File.WriteAllText(Path.Combine(prepared.Directory, "ready.json"), JsonSerializer.Serialize(prepared));
        Assert.Equal("empty-rebuild-candidate", Assert.Throws<EvidenceIndexException>(() =>
            new SqliteEvidenceIndexRebuildRecovery().Inspect(Options, prepared.Directory)).Code);
        Assert.Equal(0, new FileInfo(prepared.DatabasePath).Length);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
