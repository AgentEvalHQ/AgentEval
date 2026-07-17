// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Skills;
using Xunit;

namespace AgentEval.Tests.Skills;

/// <summary>Agent Skills Wave 1 (§4.1) — the baseline ledger's persistence: one file per snapshot, NEVER overwritten (append-only history), unlike the single-pin Gatekeeper calibration-report store.</summary>
public class JsonFileSkillBaselineStoreTests : IDisposable
{
    private readonly string _root;

    public JsonFileSkillBaselineStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agenteval-skill-baseline-store-test-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static SkillBaselineSnapshot Snapshot(DateTimeOffset capturedAt, string scannedRoot = "/repo/skills") =>
        new(
            Id: Guid.NewGuid().ToString("N"),
            CapturedAt: capturedAt,
            ScannedRoot: scannedRoot,
            Skills:
            [
                new SkillBaselineEntry(
                    Name: "example-skill",
                    SourceKind: SkillSourceKind.File,
                    RelativePath: "example-skill",
                    StructuralFingerprint: "abc123",
                    ContentHash: "def456",
                    Source: null,
                    SourceUrl: null,
                    Ref: null,
                    ComplianceFindings: []),
            ]);

    [Fact]
    public async Task SaveThenLoad_RoundTripsAllFields()
    {
        var store = new JsonFileSkillBaselineStore(_root);
        var original = Snapshot(new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero));

        var savedId = await store.SaveAsync(original);
        Assert.Equal(original.Id, savedId);

        var loaded = await store.LoadAsync(original.Id);

        Assert.NotNull(loaded);
        Assert.Equal(original.Id, loaded!.Id);
        Assert.Equal(original.CapturedAt, loaded.CapturedAt);
        Assert.Equal(original.ScannedRoot, loaded.ScannedRoot);
        var skill = Assert.Single(loaded.Skills);
        Assert.Equal("example-skill", skill.Name);
        Assert.Equal("abc123", skill.StructuralFingerprint);
        Assert.Equal("def456", skill.ContentHash);
    }

    [Fact]
    public async Task Load_NeverSaved_ReturnsNull()
    {
        var store = new JsonFileSkillBaselineStore(_root);
        var loaded = await store.LoadAsync("does-not-exist");
        Assert.Null(loaded);
    }

    [Fact]
    public async Task Save_TwoSnapshots_ProducesTwoFiles_NeverOverwrites()
    {
        var store = new JsonFileSkillBaselineStore(_root);
        var first = Snapshot(new DateTimeOffset(2026, 7, 17, 10, 0, 0, TimeSpan.Zero));
        var second = Snapshot(new DateTimeOffset(2026, 7, 17, 11, 0, 0, TimeSpan.Zero));

        await store.SaveAsync(first);
        await store.SaveAsync(second);

        var files = Directory.GetFiles(_root, "skills-baseline-*.json");
        Assert.Equal(2, files.Length);

        var all = await store.ListAsync();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task Save_SameSecond_DifferentSnapshots_BothPersist_NoFilenameCollision()
    {
        // Same CapturedAt (to the second AND millisecond) for two DIFFERENT snapshots — the Id suffix in
        // the filename must still keep them from colliding (this is exactly the risk
        // SkillBaselineSnapshot.Id's own remarks flag: "timestamps alone can collide... on fast test runs").
        var store = new JsonFileSkillBaselineStore(_root);
        var sameInstant = new DateTimeOffset(2026, 7, 17, 12, 0, 0, 123, TimeSpan.Zero);
        var a = Snapshot(sameInstant);
        var b = Snapshot(sameInstant);

        await store.SaveAsync(a);
        await store.SaveAsync(b);

        var all = await store.ListAsync();
        Assert.Equal(2, all.Count);
        Assert.NotNull(await store.LoadAsync(a.Id));
        Assert.NotNull(await store.LoadAsync(b.Id));
    }

    [Fact]
    public async Task ListAsync_OrdersOldestToNewest_ByCapturedAt_NotSaveOrder()
    {
        var store = new JsonFileSkillBaselineStore(_root);
        var newer = Snapshot(new DateTimeOffset(2026, 7, 17, 15, 0, 0, TimeSpan.Zero));
        var older = Snapshot(new DateTimeOffset(2026, 7, 17, 9, 0, 0, TimeSpan.Zero));

        // Save NEWER first, older second — ListAsync must still return oldest-first.
        await store.SaveAsync(newer);
        await store.SaveAsync(older);

        var all = await store.ListAsync();

        Assert.Equal(2, all.Count);
        Assert.Equal(older.Id, all[0].Id);
        Assert.Equal(newer.Id, all[1].Id);
    }

    [Fact]
    public async Task ListAsync_NothingSavedYet_ReturnsEmpty()
    {
        var store = new JsonFileSkillBaselineStore(_root);
        var all = await store.ListAsync();
        Assert.Empty(all);
    }

    [Fact]
    public async Task Load_CorruptFile_TreatedAsMissing_NotThrown()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "skills-baseline-20260717120000000-corrupt.json"), "{ not valid json");

        var store = new JsonFileSkillBaselineStore(_root);
        var all = await store.ListAsync();

        Assert.Empty(all);   // corrupt file skipped, not thrown
    }
}
