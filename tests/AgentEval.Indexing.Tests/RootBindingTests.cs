// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Indexing.Tests;

public sealed class RootBindingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ae-root-binding-" + Guid.NewGuid().ToString("N"));
    private EvidenceIndexOptions Options => new() { DatabasePath = Path.Combine(_root, "index.sqlite") };
    private SqliteEvidenceIndexWriter Writer() => new(Options, new SqliteEvidenceIndexSchema());
    private EvidenceIndexRoot Root(string id, string path, IndexedEvidenceKind kind = IndexedEvidenceKind.Legacy)
    { var directory = Path.Combine(_root, path); Directory.CreateDirectory(directory); return new(id, directory, kind); }

    [Fact]
    public void BindRoots_ReopenedIndex_RefusesDirectoryAndFormatRebinding()
    {
        var root = Root("root", "first"); var second = Root("root", "second");
        using (var writer = Writer()) writer.BindRoots([root]);
        var schema = new SqliteEvidenceIndexSchema(); var before = schema.EnsureCurrent(Options);
        using (var writer = Writer())
        {
            writer.BindRoots([root with { Directory = root.Directory + Path.DirectorySeparatorChar }]);
            Assert.Equal("root-binding-conflict", Assert.Throws<EvidenceIndexException>(() => writer.BindRoots([second])).Code);
            Assert.Equal("root-binding-conflict", Assert.Throws<EvidenceIndexException>(() => writer.BindRoots([root with { Kind = IndexedEvidenceKind.Aef }])).Code);
        }
        Assert.Equal(before, schema.EnsureCurrent(Options));
    }

    [Fact]
    public void BindRoots_ConflictingBatch_RollsBackEarlierNewBinding()
    {
        var root = Root("z-existing", "first"); var second = Root("z-existing", "second");
        using var writer = Writer(); writer.BindRoots([root]);
        Assert.Throws<EvidenceIndexException>(() => writer.BindRoots([Root("a-new", "new"), second]));
        // If the earlier insertion had leaked, this different location would refuse.
        writer.BindRoots([Root("a-new", "different")]);
    }

    [Fact]
    public void BindRoots_UnboundHistoricalRows_RequiresRebuildInsteadOfInventingDirectoryProvenance()
    {
        var root = Root("root", "source");
        using var writer = Writer();
        writer.Upsert(new("root", "run", new string('a', 64), IndexedEvidenceKind.Legacy, IndexedVerification.Unverified,
            new("run", null, null, null, null, null, null, "unknown"), [], [], null));
        Assert.Equal("unbound-existing-root", Assert.Throws<EvidenceIndexException>(() => writer.BindRoots([root])).Code);
        Assert.NotNull(writer.Find("root", "run"));
    }

    [Fact]
    public void Upsert_BoundRootDifferentEvidenceKind_RefusesWithoutRows()
    {
        var root = Root("root", "source", IndexedEvidenceKind.Aef);
        using var writer = Writer(); writer.BindRoots([root]);
        Assert.Equal("root-binding-conflict", Assert.Throws<EvidenceIndexException>(() => writer.Upsert(new("root", "run", new string('a', 64),
            IndexedEvidenceKind.Legacy, IndexedVerification.Unverified, new("run", null, null, null, null, null, null, "unknown"), [], [], null))).Code);
        Assert.Empty(writer.ListSources("root"));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
