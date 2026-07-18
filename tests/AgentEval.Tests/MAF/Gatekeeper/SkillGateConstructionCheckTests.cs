// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using AgentEval.MAF.Gatekeeper;
using AgentEval.MAF.Skills;
using AgentEval.Skills;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>
/// SkillGate Tier 1 (runtime governance, P0+P1) — direct unit coverage for
/// <see cref="SkillGateConstructionCheck.CheckAndEnforce"/>. End-to-end wiring through <c>UseGatekeeper</c>
/// lives in <c>AgentEvalGatekeeperExtensionsTests</c>.
/// </summary>
public class SkillGateConstructionCheckTests : IDisposable
{
    private readonly string _baselinePath;

    public SkillGateConstructionCheckTests()
    {
        _baselinePath = Path.Combine(Path.GetTempPath(), "agenteval-skillgate-test-" + Guid.NewGuid().ToString("N") + ".json");
    }

    public void Dispose()
    {
        try { if (File.Exists(_baselinePath)) File.Delete(_baselinePath); } catch { /* best-effort cleanup */ }
    }

    private static SkillManifest Manifest(string name, string description = "desc") =>
        new(name, description, [], [], null, [], SkillSourceKind.File, name);

    private static ScannedSkillInfo Scanned(string name, string description = "desc", string? absolutePath = null) =>
        new(Manifest(name, description), absolutePath);

    // ── Strict mode (default) ──

    [Fact]
    public void Strict_UnknownSkill_NoBaselineFileYet_Throws()
    {
        var ex = Assert.Throws<SkillDriftException>(
            () => SkillGateConstructionCheck.CheckAndEnforce([Scanned("a")], _baselinePath, SkillGateMode.Strict));

        var finding = Assert.Single(ex.Findings);
        Assert.Equal("a", finding.Key);
        Assert.Equal(ManifestDriftKind.New, finding.Kind);
        Assert.Contains("a", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no baseline entry", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Strict_KnownUnchangedSkill_DoesNotThrow()
    {
        var baseline = SkillManifestBaseline.Capture([Manifest("a")]);
        baseline.SaveAsync(_baselinePath).GetAwaiter().GetResult();

        var ex = Record.Exception(
            () => SkillGateConstructionCheck.CheckAndEnforce([Scanned("a")], _baselinePath, SkillGateMode.Strict));

        Assert.Null(ex);
    }

    [Fact]
    public void Strict_ChangedSkill_Throws()
    {
        var baseline = SkillManifestBaseline.Capture([Manifest("a", "original description")]);
        baseline.SaveAsync(_baselinePath).GetAwaiter().GetResult();

        var ex = Assert.Throws<SkillDriftException>(() => SkillGateConstructionCheck.CheckAndEnforce(
            [Scanned("a", "description changed after trust-pinning")], _baselinePath, SkillGateMode.Strict));

        var finding = Assert.Single(ex.Findings);
        Assert.Equal("a", finding.Key);
        Assert.Equal(ManifestDriftKind.Changed, finding.Kind);
        Assert.Contains("content changed since baseline", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Strict_MultipleUnknownSkills_ThrowsWithAllNames()
    {
        var ex = Assert.Throws<SkillDriftException>(() => SkillGateConstructionCheck.CheckAndEnforce(
            [Scanned("a"), Scanned("b")], _baselinePath, SkillGateMode.Strict));

        Assert.Equal(2, ex.Findings.Count);
        Assert.Contains("a", ex.Message, StringComparison.Ordinal);
        Assert.Contains("b", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Strict_RemovedSkill_DoesNotThrow()
    {
        // A skill present in the baseline but no longer configured is a REDUCTION of surface, not a rug-pull
        // — matches PromptTemplateDriftGate's own precedent of ignoring Removed.
        var baseline = SkillManifestBaseline.Capture([Manifest("a"), Manifest("b")]);
        baseline.SaveAsync(_baselinePath).GetAwaiter().GetResult();

        var ex = Record.Exception(
            () => SkillGateConstructionCheck.CheckAndEnforce([Scanned("a")], _baselinePath, SkillGateMode.Strict));

        Assert.Null(ex);
    }

    // ── Bootstrap mode ──

    [Fact]
    public void Bootstrap_UnknownSkill_DoesNotThrow_AndPersistsBaseline()
    {
        SkillGateConstructionCheck.CheckAndEnforce([Scanned("a")], _baselinePath, SkillGateMode.Bootstrap);

        Assert.True(File.Exists(_baselinePath));
        var persisted = SkillManifestBaseline.LoadAsync(_baselinePath).GetAwaiter().GetResult();
        Assert.True(persisted.HashesBySkillName.ContainsKey("a"));
    }

    [Fact]
    public void Bootstrap_CreatesParentDirectory_WhenItDoesNotExist()
    {
        var nestedPath = Path.Combine(Path.GetTempPath(), "agenteval-skillgate-nested-" + Guid.NewGuid().ToString("N"), "sub", "baseline.json");
        try
        {
            SkillGateConstructionCheck.CheckAndEnforce([Scanned("a")], nestedPath, SkillGateMode.Bootstrap);
            Assert.True(File.Exists(nestedPath));
        }
        finally
        {
            var top = Path.GetDirectoryName(Path.GetDirectoryName(nestedPath))!;
            try { Directory.Delete(top, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Bootstrap_SecondConstructionAfterFirstBootstrap_KnownUnchangedSkill_DoesNotThrow()
    {
        SkillGateConstructionCheck.CheckAndEnforce([Scanned("a")], _baselinePath, SkillGateMode.Bootstrap);

        var ex = Record.Exception(
            () => SkillGateConstructionCheck.CheckAndEnforce([Scanned("a")], _baselinePath, SkillGateMode.Bootstrap));

        Assert.Null(ex);
    }

    [Fact]
    public void Bootstrap_SecondConstruction_ChangedSkill_StillThrows()
    {
        // Bootstrap only relaxes the UNKNOWN-skill case — a skill already known-and-then-changed always blocks,
        // regardless of mode. This is the actual tamper signal SkillGate exists to catch.
        SkillGateConstructionCheck.CheckAndEnforce([Scanned("a", "original")], _baselinePath, SkillGateMode.Bootstrap);

        var ex = Assert.Throws<SkillDriftException>(() => SkillGateConstructionCheck.CheckAndEnforce(
            [Scanned("a", "tampered after bootstrap")], _baselinePath, SkillGateMode.Bootstrap));

        Assert.Equal(ManifestDriftKind.Changed, Assert.Single(ex.Findings).Kind);
    }

    [Fact]
    public void Bootstrap_PreservesExistingEntries_ForSkillsNotInCurrentSet()
    {
        var baseline = SkillManifestBaseline.Capture([Manifest("existing")]);
        baseline.SaveAsync(_baselinePath).GetAwaiter().GetResult();

        // Only "new-one" is in the current scan — "existing" isn't present, which is a Removed finding
        // (never blocking, never rewritten) rather than something Bootstrap's persistence should touch.
        SkillGateConstructionCheck.CheckAndEnforce([Scanned("new-one")], _baselinePath, SkillGateMode.Bootstrap);

        var persisted = SkillManifestBaseline.LoadAsync(_baselinePath).GetAwaiter().GetResult();
        Assert.True(persisted.HashesBySkillName.ContainsKey("existing"));
        Assert.True(persisted.HashesBySkillName.ContainsKey("new-one"));
    }

    // ── Content-hash signal (stronger than the structural fingerprint alone) ──

    [Fact]
    public void ContentHashDrift_CaughtEvenWhenStructuralFingerprintUnchanged()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agenteval-skillgate-content-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "original body");
            var manifest = Manifest("a");
            var structuralHash = SkillManifestPoisoningGate.Fingerprint(manifest);
            // A DIFFERENT (stale/wrong) content hash in the baseline than what's on disk now — simulates a
            // resource file's bytes having changed since trust-pinning, with the frontmatter left untouched.
            var baseline = new SkillManifestBaseline(
                DateTimeOffset.UtcNow,
                new Dictionary<string, string> { ["a"] = structuralHash },
                ContentHashesBySkillName: new Dictionary<string, string> { ["a"] = "stale-content-hash-does-not-match-anything" });
            baseline.SaveAsync(_baselinePath).GetAwaiter().GetResult();

            var ex = Assert.Throws<SkillDriftException>(() => SkillGateConstructionCheck.CheckAndEnforce(
                [new ScannedSkillInfo(manifest, dir)], _baselinePath, SkillGateMode.Strict));

            Assert.Equal(ManifestDriftKind.Changed, Assert.Single(ex.Findings).Kind);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ContentHash_NotBaselined_SkillWithFolder_StructuralOnlyStillApplies()
    {
        // Baseline never captured a content hash for "a" (e.g. an older baseline, or Tier 1 structural-only
        // usage) — the content-hash pass must be a no-op for it, not an error or a spurious finding.
        var dir = Path.Combine(Path.GetTempPath(), "agenteval-skillgate-nocontent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var manifest = Manifest("a");
            var baseline = SkillManifestBaseline.Capture([manifest]);   // structural only, no ContentHashesBySkillName
            baseline.SaveAsync(_baselinePath).GetAwaiter().GetResult();

            var ex = Record.Exception(() => SkillGateConstructionCheck.CheckAndEnforce(
                [new ScannedSkillInfo(manifest, dir)], _baselinePath, SkillGateMode.Strict));

            Assert.Null(ex);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ContentHash_NoAbsolutePath_NonFileSkill_SkipsContentCheck_StructuralStillApplies()
    {
        var manifest = Manifest("a");
        var baseline = new SkillManifestBaseline(
            DateTimeOffset.UtcNow,
            new Dictionary<string, string> { ["a"] = SkillManifestPoisoningGate.Fingerprint(manifest) },
            ContentHashesBySkillName: new Dictionary<string, string> { ["a"] = "irrelevant-since-no-folder-to-hash" });
        baseline.SaveAsync(_baselinePath).GetAwaiter().GetResult();

        // AbsolutePath is null (non-file-sourced skill) — the content pass must skip it, not throw for a
        // hash it structurally cannot compute.
        var ex = Record.Exception(() => SkillGateConstructionCheck.CheckAndEnforce(
            [new ScannedSkillInfo(manifest, AbsolutePath: null)], _baselinePath, SkillGateMode.Strict));

        Assert.Null(ex);
    }

    [Fact]
    public void ContentHash_AbsolutePathSetButFolderMissing_SkipsContentCheck_DoesNotThrow()
    {
        // Regression test for a real bug found in review: HashSkillFolder throws DirectoryNotFoundException
        // for a non-existent folder, and CheckAndEnforce runs at agent CONSTRUCTION time — a skill folder
        // moved/deleted since the baseline was captured must not crash startup for a reason unrelated to
        // actual skill drift. The structural fingerprint check still fully applies.
        var missingDir = Path.Combine(Path.GetTempPath(), "agenteval-skillgate-missing-" + Guid.NewGuid().ToString("N"));
        var manifest = Manifest("a");
        var baseline = new SkillManifestBaseline(
            DateTimeOffset.UtcNow,
            new Dictionary<string, string> { ["a"] = SkillManifestPoisoningGate.Fingerprint(manifest) },
            ContentHashesBySkillName: new Dictionary<string, string> { ["a"] = "irrelevant-since-folder-is-gone" });
        baseline.SaveAsync(_baselinePath).GetAwaiter().GetResult();

        var ex = Record.Exception(() => SkillGateConstructionCheck.CheckAndEnforce(
            [new ScannedSkillInfo(manifest, missingDir)], _baselinePath, SkillGateMode.Strict));

        Assert.Null(ex);
    }

    [Fact]
    public void Strict_SkillRenamedCaseOnly_ReportsChanged_NotNewPlusRemoved()
    {
        // Regression test for a real bug found in review: a skill recased ("a" -> "A") with content otherwise
        // unchanged must still be recognized as the SAME skill (a Changed finding), not an unrelated New+Removed
        // pair — a case-only rename is exactly the rug-pull-evasion vector this comparison must not fall for
        // (mirrors SkillsScanCommand's own --save-manifest-baseline lowercasing convention).
        var baseline = SkillManifestBaseline.Capture([Manifest("a", "original description")]);
        baseline.SaveAsync(_baselinePath).GetAwaiter().GetResult();

        var ex = Assert.Throws<SkillDriftException>(() => SkillGateConstructionCheck.CheckAndEnforce(
            [Scanned("A", "description changed after trust-pinning")], _baselinePath, SkillGateMode.Strict));

        var finding = Assert.Single(ex.Findings);
        Assert.Equal(ManifestDriftKind.Changed, finding.Kind);
    }

    [Fact]
    public void NullSkills_Throws()
        => Assert.Throws<ArgumentNullException>(() => SkillGateConstructionCheck.CheckAndEnforce(null!, _baselinePath, SkillGateMode.Strict));

    [Fact]
    public void EmptyBaselinePath_Throws()
        => Assert.Throws<ArgumentException>(() => SkillGateConstructionCheck.CheckAndEnforce([Scanned("a")], "", SkillGateMode.Strict));
}
