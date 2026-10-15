// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;

namespace AgentEval.Indexing.Tests;

public sealed class RebuildPreparationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ae-index-rebuild-test-" + Guid.NewGuid().ToString("N"));
    private EvidenceIndexOptions Options => new() { DatabasePath = Path.Combine(_root, "active.sqlite") };
    private static EvidenceIndexSource Source(string path) => new("root", path, new string('a', 64),
        IndexedEvidenceKind.Legacy, IndexedVerification.Unverified,
        new(path, null, "agent:fixture", null, null, null, "PASS", "unknown"), [], [], null);
    public RebuildPreparationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Prepare_ActiveWriterAndNewEvidence_PreservesCurrentIndexAndValidatesDetachedCandidate()
    {
        var schema = new SqliteEvidenceIndexSchema();
        using var current = new SqliteEvidenceIndexWriter(Options, schema);
        current.Upsert(Source("old"));
        var before = ReadWhileWriterIdle();
        var prepared = new EvidenceIndexRebuildPreparation(schema).Prepare(Options, (writer, token) =>
        {
            writer.Upsert(Source("new"), token); return true;
        });
        Assert.Equal(before, ReadWhileWriterIdle());
        Assert.NotNull(current.Find("root", "old")); Assert.Null(current.Find("root", "new"));
        Assert.Equal(1, prepared.Schema.Generation);
        Assert.Equal(Options.DatabasePath, prepared.TargetDatabasePath);
        var receipt = JsonSerializer.Deserialize<PreparedEvidenceIndex>(File.ReadAllText(Path.Combine(prepared.Directory, "ready.json")));
        Assert.Equal(prepared, receipt);
        using var candidate = new SqliteEvidenceIndexWriter(new() { DatabasePath = prepared.DatabasePath }, schema);
        Assert.NotNull(candidate.Find("root", "new")); Assert.Null(candidate.Find("root", "old"));

        byte[] ReadWhileWriterIdle()
        {
            // The test owns the only writer and performs no concurrent mutation. SQLite's Windows
            // handle requires sharing write access even for this read-only byte comparison.
            using var file = new FileStream(Options.DatabasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var copy = new MemoryStream(); file.CopyTo(copy); return copy.ToArray();
        }
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("exception")]
    [InlineData("cancel")]
    [InlineData("capacity")]
    public void Prepare_FailedPopulation_RemovesOnlyOwnScratchAndPreservesTarget(string failure)
    {
        File.WriteAllText(Options.DatabasePath, "corrupt original remains recoverable");
        var before = File.ReadAllBytes(Options.DatabasePath);
        using var cancellation = new CancellationTokenSource();
        var service = new EvidenceIndexRebuildPreparation(new SqliteEvidenceIndexSchema());
        var error = Record.Exception(() => service.Prepare(Options, (writer, token) =>
        {
            writer.Upsert(Source("one"), token);
            if (failure == "exception") throw new InvalidOperationException("test population failed");
            if (failure == "cancel") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            if (failure == "capacity") writer.Upsert(Source("two"), token);
            return false;
        }, new() { MaxSources = 1 }, cancellation.Token));
        Assert.NotNull(error);
        Assert.Equal(before, File.ReadAllBytes(Options.DatabasePath));
        Assert.Empty(Directory.EnumerateDirectories(_root));
        Assert.Equal(2, Directory.EnumerateFiles(_root).Count()); // Original plus reusable preparation lock.
    }

    [Fact]
    public void Prepare_CorruptTarget_BuildsSeparatelyWithoutOpeningOrReplacingIt()
    {
        File.WriteAllText(Options.DatabasePath, "invalid SQLite");
        var prepared = new EvidenceIndexRebuildPreparation(new SqliteEvidenceIndexSchema()).Prepare(Options, (writer, token) =>
        { writer.Upsert(Source("replacement"), token); return true; });
        Assert.Equal("invalid SQLite", File.ReadAllText(Options.DatabasePath));
        Assert.Equal(prepared.Schema, new SqliteEvidenceIndexSchema().EnsureCurrent(new() { DatabasePath = prepared.DatabasePath }));
        using var file = File.OpenRead(prepared.DatabasePath);
        Assert.Equal(prepared.ContentDigest, EvidenceIndexRebuildPreparation.Hash(file, Options.MaxDatabaseBytes, default));
    }

    [Fact]
    public void Prepare_MissingTarget_DoesNotCreateItBeforePublication()
    {
        var prepared = new EvidenceIndexRebuildPreparation(new SqliteEvidenceIndexSchema()).Prepare(Options, (_, _) => true);
        Assert.False(File.Exists(Options.DatabasePath));
        Assert.True(File.Exists(prepared.DatabasePath));
        Assert.Equal(0, prepared.Schema.Generation);
    }

    [Fact]
    public void Prepare_CandidateCapacity_RefusesBeforePopulationAndRetainsPreparedCandidates()
    {
        var service = new EvidenceIndexRebuildPreparation(new SqliteEvidenceIndexSchema());
        for (var i = 0; i < 8; i++) service.Prepare(Options, (_, _) => true);
        var called = false;
        var error = Assert.Throws<EvidenceIndexException>(() => service.Prepare(Options, (_, _) => { called = true; return true; }));
        Assert.Equal("rebuild-candidate-limit", error.Code);
        Assert.False(called);
        Assert.Equal(8, Directory.EnumerateDirectories(_root).Count());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
