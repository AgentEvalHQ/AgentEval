// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;

namespace AgentEval.Indexing.Tests;

public sealed class RebuildPromotionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ae-index-promote-test-" + Guid.NewGuid().ToString("N"));
    private EvidenceIndexOptions Options => new() { DatabasePath = Path.Combine(_root, "active.sqlite") };
    public RebuildPromotionTests() => Directory.CreateDirectory(_root);
    private PreparedEvidenceIndex Prepare() => new EvidenceIndexRebuildPreparation(new SqliteEvidenceIndexSchema()).Prepare(Options, (_, _) => true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Promote_AbsentOrCorruptTarget_PublishesValidatedIndexAndSupportsRestartRetry(bool existing)
    {
        if (existing) File.WriteAllText(Options.DatabasePath, "damaged previous index");
        var promotion = new SqliteEvidenceIndexRebuildPromotion();
        var expected = promotion.CaptureTarget(Options);
        var prepared = Prepare();
        var result = promotion.Promote(Options, prepared.Directory, expected);
        Assert.False(result.AlreadyPublished);
        Assert.Equal(prepared.Schema.InstanceId, new SqliteEvidenceIndexSchema().EnsureCurrent(Options).InstanceId);
        if (existing) Assert.Equal("damaged previous index", File.ReadAllText(result.PreviousDatabasePath!));
        else Assert.Null(result.PreviousDatabasePath);
        var retry = new SqliteEvidenceIndexRebuildPromotion().Promote(Options, prepared.Directory, expected);
        Assert.True(retry.AlreadyPublished);
        Assert.Equal(result.InstanceId, retry.InstanceId);
        new SqliteEvidenceIndexRebuildRecovery().Discard(Options, prepared.Directory);
        Assert.Equal(prepared.Schema.InstanceId, new SqliteEvidenceIndexSchema().EnsureCurrent(Options).InstanceId);
    }

    [Fact]
    public void Promote_ValidTarget_KeepsExactPreviousDatabaseAndChangesInstance()
    {
        var old = new SqliteEvidenceIndexSchema().EnsureCurrent(Options);
        var bytes = File.ReadAllBytes(Options.DatabasePath);
        var promotion = new SqliteEvidenceIndexRebuildPromotion();
        var expected = promotion.CaptureTarget(Options);
        var prepared = Prepare();
        var result = promotion.Promote(Options, prepared.Directory, expected);
        Assert.NotEqual(old.InstanceId, result.InstanceId);
        Assert.Equal(bytes, File.ReadAllBytes(result.PreviousDatabasePath!));
    }

    [Fact]
    public void Promote_StaleTarget_RefusesBeforeWritingIntentOrChangingEitherDatabase()
    {
        var promotion = new SqliteEvidenceIndexRebuildPromotion();
        var expected = promotion.CaptureTarget(Options);
        var prepared = Prepare();
        File.WriteAllText(Options.DatabasePath, "newly appeared index");
        Assert.Equal("stale-rebuild-target", Assert.Throws<EvidenceIndexException>(() => promotion.Promote(Options, prepared.Directory, expected)).Code);
        Assert.Equal("newly appeared index", File.ReadAllText(Options.DatabasePath));
        Assert.False(File.Exists(Path.Combine(prepared.Directory, "promotion.json")));
        Assert.True(new SqliteEvidenceIndexRebuildRecovery().Inspect(Options, prepared.Directory).Ready);
    }

    [Fact]
    public void Promote_TargetWriterOrSidecar_Refuses()
    {
        var promotion = new SqliteEvidenceIndexRebuildPromotion();
        var expected = promotion.CaptureTarget(Options);
        var prepared = Prepare();
        using (var writer = new SqliteEvidenceIndexWriter(Options, new SqliteEvidenceIndexSchema()))
            Assert.ThrowsAny<IOException>(() => promotion.Promote(Options, prepared.Directory, expected));
        File.WriteAllText(Options.DatabasePath + "-journal", "retain journal");
        Assert.Equal("target-sidecars-present", Assert.Throws<EvidenceIndexException>(() => promotion.CaptureTarget(Options)).Code);
        Assert.Equal("retain journal", File.ReadAllText(Options.DatabasePath + "-journal"));
    }

    [Fact]
    public void Promote_InterruptedPublishCopy_RebuildsCopyFromValidatedCandidate()
    {
        var promotion = new SqliteEvidenceIndexRebuildPromotion();
        var expected = promotion.CaptureTarget(Options);
        var prepared = Prepare();
        var intent = new SqliteEvidenceIndexRebuildPromotion.PromotionIntent(1, expected, prepared.ContentDigest, prepared.Schema.InstanceId);
        File.WriteAllText(Path.Combine(prepared.Directory, "promotion.json"), JsonSerializer.Serialize(intent));
        File.WriteAllText(Path.Combine(prepared.Directory, "publish.sqlite"), "partial copy");
        Assert.False(promotion.Promote(Options, prepared.Directory, expected).AlreadyPublished);
        Assert.Equal(prepared.Schema.InstanceId, new SqliteEvidenceIndexSchema().EnsureCurrent(Options).InstanceId);
    }

    [Fact]
    public void Promote_InterruptedAfterBackupBeforeReplacement_VerifiesBackupThenRetries()
    {
        File.WriteAllText(Options.DatabasePath, "old bytes");
        var promotion = new SqliteEvidenceIndexRebuildPromotion();
        var expected = promotion.CaptureTarget(Options);
        var prepared = Prepare();
        var intent = new SqliteEvidenceIndexRebuildPromotion.PromotionIntent(1, expected, prepared.ContentDigest, prepared.Schema.InstanceId);
        File.WriteAllText(Path.Combine(prepared.Directory, "promotion.json"), JsonSerializer.Serialize(intent));
        File.Copy(Options.DatabasePath, Path.Combine(prepared.Directory, "previous.sqlite"));
        var result = promotion.Promote(Options, prepared.Directory, expected);
        Assert.Equal("old bytes", File.ReadAllText(result.PreviousDatabasePath!));
        Assert.Equal(prepared.Schema.InstanceId, new SqliteEvidenceIndexSchema().EnsureCurrent(Options).InstanceId);
    }

    [Fact]
    public void Promote_PublishedTargetThenChanged_RefusesInsteadOfRollingBackNewWork()
    {
        var promotion = new SqliteEvidenceIndexRebuildPromotion();
        var expected = promotion.CaptureTarget(Options);
        var prepared = Prepare();
        promotion.Promote(Options, prepared.Directory, expected);
        File.WriteAllText(Options.DatabasePath, "later work");
        Assert.Equal("stale-rebuild-target", Assert.Throws<EvidenceIndexException>(() => promotion.Promote(Options, prepared.Directory, expected)).Code);
        Assert.Equal("later work", File.ReadAllText(Options.DatabasePath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
