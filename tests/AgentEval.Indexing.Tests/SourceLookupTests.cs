// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Indexing.Tests;

public sealed class SourceLookupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ae-source-lookup-" + Guid.NewGuid().ToString("N"));
    private EvidenceIndexOptions Options => new() { DatabasePath = Path.Combine(_root, "index.sqlite") };
    private static EvidenceIndexSource Source() => new("root", "run", new string('a', 64), IndexedEvidenceKind.Legacy,
        IndexedVerification.Unverified, new("run-id", null, "subject", null, null, null, "PASS", "unknown"), [], [], null);
    public SourceLookupTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Find_BoundSource_ReturnsBindingRunAndProvenanceInCurrentSnapshot()
    {
        using var writer = new SqliteEvidenceIndexWriter(Options, new SqliteEvidenceIndexSchema());
        writer.BindRoots([new("root", _root, IndexedEvidenceKind.Legacy)]);
        var mutation = writer.Upsert(Source()); var reader = new SqliteEvidenceSourceLookup(Options);
        var selected = reader.Find(mutation.SourceId)!;
        Assert.Equal(mutation.Generation, selected.Generation); Assert.Equal(mutation.Fingerprint, selected.Source.Fingerprint);
        Assert.Equal("run-id", selected.Run!.RunId); Assert.Null(selected.Run.RunHash);
        Assert.Equal(_root, selected.Binding!.Directory); Assert.Equal(IndexedEvidenceKind.Legacy, selected.Binding.Kind);
        writer.Upsert(Source() with { Run = Source().Run! with { Outcome = "FAIL" } });
        Assert.Equal("PASS", selected.Run.Outcome); Assert.Equal("FAIL", reader.Find(mutation.SourceId)!.Run!.Outcome);
    }

    [Fact]
    public void Find_UnboundSourceMissingAndInvalidInput_RemainDistinct()
    {
        using var writer = new SqliteEvidenceIndexWriter(Options, new SqliteEvidenceIndexSchema());
        var id = writer.Upsert(Source()).SourceId; var reader = new SqliteEvidenceSourceLookup(Options);
        Assert.Null(reader.Find(id)!.Binding); Assert.Null(reader.Find(new string('0', 64)));
        Assert.Equal("invalid-page-request", Assert.Throws<EvidenceIndexException>(() => reader.Find("../run")).Code);
        Assert.Throws<OperationCanceledException>(() => reader.Find(id, new(true)));
    }

    [Fact]
    public void Find_OversizedRunMetadata_RefusesBeforeMaterializingText()
    {
        using var writer = new SqliteEvidenceIndexWriter(Options, new SqliteEvidenceIndexSchema());
        var id = writer.Upsert(Source() with { Run = Source().Run! with { RunId = new string('x', (2 << 20) + 1) } }).SourceId;
        Assert.Equal("page-byte-limit", Assert.Throws<EvidenceIndexException>(() => new SqliteEvidenceSourceLookup(Options).Find(id)).Code);
    }

    [Fact]
    public void Find_MissingIndex_DoesNotCreateIt()
    {
        Assert.Equal("missing-index", Assert.Throws<EvidenceIndexException>(() => new SqliteEvidenceSourceLookup(Options).Find(new string('a', 64))).Code);
        Assert.False(File.Exists(Options.DatabasePath));
    }
    public void Dispose() => Directory.Delete(_root, recursive: true);
}
