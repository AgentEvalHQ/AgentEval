// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;

namespace AgentEval.Indexing.Tests;

public sealed class SourceReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ae-source-page-" + Guid.NewGuid().ToString("N"));
    private EvidenceIndexOptions Options => new() { DatabasePath = Path.Combine(_root, "index.sqlite") };
    public SourceReaderTests() => Directory.CreateDirectory(_root);
    private SqliteEvidenceIndexWriter Writer() => new(Options, new SqliteEvidenceIndexSchema());
    private static EvidenceIndexSource Source(string root, string path) => new(root, path, new string('a', 64), IndexedEvidenceKind.Legacy,
        IndexedVerification.Unverified, new(path, null, null, null, null, null, "PASS", "unknown"), [], [], null);

    [Fact]
    public void Read_MultipleRootsAndUnicodePaths_PaginatesExactlyOnceWhileWriterRemainsOpen()
    {
        using var writer = Writer();
        string[] roots = ["b", "a"]; string[] paths = ["z", "a", "é", "\uE000", "😀"];
        foreach (var root in roots) foreach (var path in paths) writer.Upsert(Source(root, path));
        var reader = new SqliteEvidenceSourceReader(Options);
        var actual = new List<string>(); string? cursor = null;
        do
        {
            var page = reader.Read(new(PageSize: 3, Cursor: cursor));
            Assert.InRange(page.Items.Length, 1, 3); Assert.Equal(10, page.Generation);
            actual.AddRange(page.Items.Select(i => i.RootId + "/" + i.RelativePath));
            cursor = page.NextCursor;
        } while (cursor is not null);
        var expected = roots.SelectMany(r => paths.Select(p => r + "/" + p)).OrderBy(s => Convert.ToHexString(Encoding.UTF8.GetBytes(s)), StringComparer.Ordinal);
        Assert.Equal(expected, actual); Assert.Equal(10, actual.Distinct().Count());
        var scoped = reader.Read(new("a", 10)); Assert.Equal(5, scoped.Items.Length);
        Assert.All(scoped.Items, i => Assert.Equal("a", i.RootId)); Assert.Null(scoped.NextCursor);
    }

    [Fact]
    public void Read_ChangedIndex_RejectsStaleCursorButNoOpWriteKeepsItValid()
    {
        using var writer = Writer(); writer.Upsert(Source("root", "one")); writer.Upsert(Source("root", "two"));
        var reader = new SqliteEvidenceSourceReader(Options);
        var cursor = reader.Read(new(PageSize: 1)).NextCursor!;
        writer.Upsert(Source("root", "one"));
        Assert.Single(reader.Read(new(PageSize: 1, Cursor: cursor)).Items);
        writer.Upsert(Source("root", "three"));
        Assert.Equal("stale-cursor", Assert.Throws<EvidenceIndexException>(() => reader.Read(new(PageSize: 1, Cursor: cursor))).Code);
    }

    [Fact]
    public void Read_RebuiltIndexSameGeneration_RejectsOldInstanceCursor()
    {
        using (var writer = Writer()) { writer.Upsert(Source("root", "one")); writer.Upsert(Source("root", "two")); }
        var reader = new SqliteEvidenceSourceReader(Options);
        var first = reader.Read(new(PageSize: 1));
        var promotion = new SqliteEvidenceIndexRebuildPromotion(); var target = promotion.CaptureTarget(Options);
        var prepared = new EvidenceIndexRebuildPreparation(new SqliteEvidenceIndexSchema()).Prepare(Options, (writer, _) =>
        { writer.Upsert(Source("root", "one")); writer.Upsert(Source("root", "two")); return true; });
        promotion.Promote(Options, prepared.Directory, target);
        Assert.Equal(first.Generation, reader.Read(new()).Generation);
        Assert.Equal("stale-cursor", Assert.Throws<EvidenceIndexException>(() => reader.Read(new(PageSize: 1, Cursor: first.NextCursor))).Code);
    }

    [Fact]
    public void Read_MalformedOrDifferentQueryCursor_Refuses()
    {
        using var writer = Writer(); writer.Upsert(Source("root", "one")); writer.Upsert(Source("root", "two"));
        var reader = new SqliteEvidenceSourceReader(Options);
        var cursor = reader.Read(new(PageSize: 1)).NextCursor!;
        Assert.Equal("invalid-cursor", Assert.Throws<EvidenceIndexException>(() => reader.Read(new("root", 1, cursor))).Code);
        Assert.Equal("invalid-cursor", Assert.Throws<EvidenceIndexException>(() => reader.Read(new(PageSize: 2, Cursor: cursor))).Code);
        foreach (var malformed in new[] { "", "%%%", "e30", new string('a', 65537), cursor + "=" })
            Assert.Equal("invalid-cursor", Assert.Throws<EvidenceIndexException>(() => reader.Read(new(Cursor: malformed))).Code);
    }

    [Fact]
    public void Read_MissingIndexOrCancellation_DoesNotCreateDatabase()
    {
        var reader = new SqliteEvidenceSourceReader(Options);
        Assert.Equal("missing-index", Assert.Throws<EvidenceIndexException>(() => reader.Read(new())).Code);
        Assert.Throws<OperationCanceledException>(() => reader.Read(new(), new(true)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public void Read_ReplacementLease_RefusesUntilReleased()
    {
        using (var writer = Writer()) writer.Upsert(Source("root", "one"));
        var reader = new SqliteEvidenceSourceReader(Options);
        using (var replacement = new FileStream(Options.DatabasePath + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.ThrowsAny<IOException>(() => reader.Read(new()));
        Assert.Single(reader.Read(new()).Items);
    }

    [Fact]
    public void Read_LargeUtf8Paths_EnforcesPageBytesAndCanResumeWithSmallerPages()
    {
        using var writer = Writer();
        var tail = new string('界', 4000);
        for (var i = 0; i < 180; i++) writer.Upsert(Source("root", i.ToString("D4") + "/" + tail));
        var reader = new SqliteEvidenceSourceReader(Options);
        Assert.Equal("page-byte-limit", Assert.Throws<EvidenceIndexException>(() => reader.Read(new(PageSize: 500))).Code);
        var first = reader.Read(new(PageSize: 1)); Assert.Single(first.Items);
        var second = reader.Read(new(PageSize: 1, Cursor: first.NextCursor)); Assert.Single(second.Items);
        Assert.NotEqual(first.Items[0].RelativePath, second.Items[0].RelativePath);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
