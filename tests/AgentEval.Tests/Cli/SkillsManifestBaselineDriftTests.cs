// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Guardrails;
using AgentEval.Skills;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// Agent Skills Phase 4b CLI surface (v2 scan-verb CLI surface, the cheap half) — <c>skills scan --save-manifest-baseline</c>
/// / <c>--manifest-baseline</c>: a SINGLE-PIN, JSON-file-backed hash-pin drift gate
/// (<see cref="SkillManifestPoisoningGate"/>/<see cref="SkillManifestBaseline"/>), deliberately distinct from
/// Wave 1/2's multi-snapshot <c>--write-baseline</c>/<c>--baseline-root</c>/<c>--check-baseline</c> ledger.
/// </summary>
public class SkillsManifestBaselineDriftTests : IDisposable
{
    private readonly string _baselineFile;

    public SkillsManifestBaselineDriftTests()
    {
        _baselineFile = Path.Combine(Path.GetTempPath(), "agenteval-manifest-baseline-test-" + Guid.NewGuid().ToString("N") + ".json");
    }

    public void Dispose()
    {
        try { File.Delete(_baselineFile); } catch { /* best-effort cleanup */ }
    }

    // ── command wiring ──

    [Fact]
    public void Create_ScanSubcommand_HasManifestBaselineOptions()
    {
        var scanCmd = SkillsScanCommand.Create().Subcommands.Single(c => c.Name == "scan");
        Assert.Contains(scanCmd.Options, o => o.Name == "--save-manifest-baseline");
        Assert.Contains(scanCmd.Options, o => o.Name == "--manifest-baseline");
        Assert.Contains(scanCmd.Options, o => o.Name == "--baseline-note");
    }

    [Fact]
    public void Create_ScanWorkspaceSubcommand_HasManifestBaselineOptions()
    {
        var cmd = SkillsScanCommand.Create().Subcommands.Single(c => c.Name == "scan-workspace");
        Assert.Contains(cmd.Options, o => o.Name == "--save-manifest-baseline");
        Assert.Contains(cmd.Options, o => o.Name == "--manifest-baseline");
        Assert.Contains(cmd.Options, o => o.Name == "--baseline-note");
    }

    // ── DetectManifestDrift: pure unit tests ──

    [Fact]
    public void DetectManifestDrift_UnchangedFingerprint_NoFinding()
    {
        var entries = new[] { new SkillBaselineEntry("skill-a", SkillSourceKind.File, "skill-a", "fp-1", "content-hash", null, null, null, []) };
        var baseline = new SkillManifestBaseline(DateTimeOffset.UtcNow, new Dictionary<string, string> { ["skill-a"] = "fp-1" });

        var findings = SkillsScanCommand.DetectManifestDrift(entries, baseline);

        Assert.Empty(findings);
    }

    [Fact]
    public void DetectManifestDrift_ChangedFingerprint_ReturnsHighSeverityFinding()
    {
        var entries = new[] { new SkillBaselineEntry("skill-a", SkillSourceKind.File, "skill-a", "fp-2", "content-hash", null, null, null, []) };
        var baseline = new SkillManifestBaseline(DateTimeOffset.UtcNow, new Dictionary<string, string> { ["skill-a"] = "fp-1" });

        var findings = SkillsScanCommand.DetectManifestDrift(entries, baseline);

        var finding = Assert.Single(findings);
        Assert.Equal("skill-a", finding.SkillName);
        Assert.Equal(SkillComplianceRule.ManifestChangedSinceBaseline, finding.Rule);
        Assert.Equal(Severity.High, finding.Severity);
    }

    [Fact]
    public void DetectManifestDrift_NewSkill_NotBaselined_NoFinding()
    {
        // New (not yet pinned) is housekeeping, not a poisoning signal — deliberately not surfaced.
        var entries = new[] { new SkillBaselineEntry("skill-new", SkillSourceKind.File, "skill-new", "fp-1", "content-hash", null, null, null, []) };
        var baseline = new SkillManifestBaseline(DateTimeOffset.UtcNow, new Dictionary<string, string>());

        var findings = SkillsScanCommand.DetectManifestDrift(entries, baseline);

        Assert.Empty(findings);
    }

    [Fact]
    public void DetectManifestDrift_RemovedSkill_NoFinding()
    {
        // Removed (baselined but no longer present) is housekeeping, not a poisoning signal.
        var entries = Array.Empty<SkillBaselineEntry>();
        var baseline = new SkillManifestBaseline(DateTimeOffset.UtcNow, new Dictionary<string, string> { ["skill-gone"] = "fp-1" });

        var findings = SkillsScanCommand.DetectManifestDrift(entries, baseline);

        Assert.Empty(findings);
    }

    [Fact]
    public void DetectManifestDrift_DuplicateNameAcrossLocations_DoesNotThrow_UsesFirstDeterministically()
    {
        // The exact --repo/scan-workspace cross-location scenario: 2 entries share a Name. Must not crash
        // (a plain ToDictionary would) — GroupBy+First tolerance, same pattern DiffAsync's own
        // baselineBySkill/currentBySkill already use.
        var entries = new[]
        {
            new SkillBaselineEntry("shared", SkillSourceKind.File, "repo-a/shared", "fp-a", "hash-a", null, null, null, []),
            new SkillBaselineEntry("shared", SkillSourceKind.File, "repo-b/shared", "fp-b", "hash-b", null, null, null, []),
        };
        var baseline = new SkillManifestBaseline(DateTimeOffset.UtcNow, new Dictionary<string, string> { ["shared"] = "fp-a" });

        var findings = SkillsScanCommand.DetectManifestDrift(entries, baseline);

        Assert.Empty(findings);   // "fp-a" (first-encountered) matches the baseline — no drift reported
    }

    [Fact]
    public void DetectManifestDrift_NameCaseDiffersFromBaseline_StillDetectsAsChanged()
    {
        // A rug-pull that ALSO re-cases the skill's name: name+content both changed. Without the
        // lowercase-normalization fix, ManifestDriftDetector.Detect's own StringComparer.Ordinal key union
        // would see this as an unrelated New+Removed pair (both filtered out as housekeeping) instead of a
        // single Changed skill — a real detection bypass this test guards against.
        var entries = new[] { new SkillBaselineEntry("My-Skill", SkillSourceKind.File, "My-Skill", "fp-2", "content-hash", null, null, null, []) };
        var baseline = new SkillManifestBaseline(DateTimeOffset.UtcNow, new Dictionary<string, string> { ["my-skill"] = "fp-1" });

        var findings = SkillsScanCommand.DetectManifestDrift(entries, baseline);

        var finding = Assert.Single(findings);
        Assert.Equal(SkillComplianceRule.ManifestChangedSinceBaseline, finding.Rule);
    }

    [Fact]
    public void DetectManifestDrift_NullHashInBaseline_DoesNotThrow_ReportsMalformedInstead()
    {
        // A hand-edited/corrupted --manifest-baseline JSON file can deserialize a null hash for a present
        // key (System.Text.Json's default options don't enforce NRT non-nullability at runtime) — must
        // degrade to a clear finding, not a NullReferenceException.
        var entries = new[] { new SkillBaselineEntry("skill-a", SkillSourceKind.File, "skill-a", "fp-1", "content-hash", null, null, null, []) };
        var baseline = new SkillManifestBaseline(DateTimeOffset.UtcNow, new Dictionary<string, string> { ["skill-a"] = null! });

        var findings = SkillsScanCommand.DetectManifestDrift(entries, baseline);

        var finding = Assert.Single(findings);
        Assert.Equal(SkillComplianceRule.ManifestChangedSinceBaseline, finding.Rule);
        Assert.Contains("malformed", finding.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── ExecuteAsync: real, offline, end-to-end save + drift-check ──

    [Fact]
    public async Task ExecuteAsync_ManifestBaselineFileDoesNotExist_DoesNotCrash_ReportsCompliant()
    {
        // Graceful "nothing to compare against yet" — same precedent --check-baseline already establishes
        // for an empty ledger — not a fatal error. Lets --save-manifest-baseline + --manifest-baseline point
        // at the SAME not-yet-written path in one command on a first-ever run.
        var dir = CreateCompliantFixture(out _);
        var missingBaselinePath = Path.Combine(Path.GetTempPath(), "agenteval-missing-baseline-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var exit = await SkillsScanCommand.ExecuteAsync(
                dir, "console", null, failOnNoncompliant: true, manifestBaseline: new FileInfo(missingBaselinePath), ct: default);

            Assert.Equal(ExitCodes.Success, exit);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public async Task ExecuteAsync_SaveManifestBaseline_ParentDirectoryDoesNotExist_CreatesIt()
    {
        var dir = CreateCompliantFixture(out _);
        var nestedPath = Path.Combine(Path.GetTempPath(), "agenteval-manifest-baseline-nested-" + Guid.NewGuid().ToString("N"), "sub", "pin.json");
        try
        {
            var exit = await SkillsScanCommand.ExecuteAsync(
                dir, "console", null, false, saveManifestBaseline: new FileInfo(nestedPath), ct: default);

            Assert.Equal(ExitCodes.Success, exit);
            Assert.True(File.Exists(nestedPath));
        }
        finally
        {
            dir.Delete(recursive: true);
            try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(nestedPath))!, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task ExecuteAsync_SaveManifestBaseline_ThenManifestChanges_ManifestBaselineDetectsDrift()
    {
        var dir = CreateCompliantFixture(out var skillName);
        try
        {
            // Capture the baseline.
            var exit1 = await SkillsScanCommand.ExecuteAsync(
                dir, "console", null, false, saveManifestBaseline: new FileInfo(_baselineFile), ct: default);
            Assert.Equal(ExitCodes.Success, exit1);
            Assert.True(File.Exists(_baselineFile));

            // Change the skill's description (changes its structural fingerprint).
            var skillDir = Path.Combine(dir.FullName, skillName);
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
                $"---\nname: {skillName}\ndescription: CHANGED description for drift test.\n---\n\nBody.\n");

            var outFile = new FileInfo(Path.Combine(Path.GetTempPath(), "agenteval-manifest-drift-out-" + Guid.NewGuid().ToString("N") + ".json"));
            try
            {
                var exit2 = await SkillsScanCommand.ExecuteAsync(
                    dir, "json", outFile, failOnNoncompliant: true, manifestBaseline: new FileInfo(_baselineFile), ct: default);

                // A High-severity finding now exists, so --fail-on-noncompliant fires.
                Assert.Equal(ExitCodes.TestFailure, exit2);
                var content = await File.ReadAllTextAsync(outFile.FullName);
                Assert.Contains(skillName, content, StringComparison.Ordinal);
            }
            finally { if (outFile.Exists) outFile.Delete(); }
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public async Task ExecuteAsync_SaveManifestBaseline_ThenNoChange_ManifestBaselineFindsNoDrift()
    {
        var dir = CreateCompliantFixture(out _);
        try
        {
            var exit1 = await SkillsScanCommand.ExecuteAsync(
                dir, "console", null, false, saveManifestBaseline: new FileInfo(_baselineFile), ct: default);
            Assert.Equal(ExitCodes.Success, exit1);

            var exit2 = await SkillsScanCommand.ExecuteAsync(
                dir, "console", null, failOnNoncompliant: true, manifestBaseline: new FileInfo(_baselineFile), ct: default);

            Assert.Equal(ExitCodes.Success, exit2);   // no drift ⇒ still compliant
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public async Task ExecuteAsync_SaveManifestBaseline_WithNotes_PersistsNotes()
    {
        var dir = CreateCompliantFixture(out _);
        try
        {
            await SkillsScanCommand.ExecuteAsync(
                dir, "console", null, false, saveManifestBaseline: new FileInfo(_baselineFile),
                baselineNotes: "reviewed and approved for this test", ct: default);

            var baseline = await SkillManifestBaseline.LoadAsync(_baselineFile);
            Assert.Equal("reviewed and approved for this test", baseline.Notes);
        }
        finally { dir.Delete(recursive: true); }
    }

    private static DirectoryInfo CreateCompliantFixture(out string skillName)
    {
        skillName = "manifest-baseline-skill-" + Guid.NewGuid().ToString("N")[..8];
        var root = Path.Combine(Path.GetTempPath(), "agenteval-manifest-baseline-fixture-" + Guid.NewGuid().ToString("N"));
        var skillDir = Path.Combine(root, skillName);
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
            $"---\nname: {skillName}\ndescription: A compliant fixture skill for manifest-baseline tests.\n---\n\nBody.\n");
        return new DirectoryInfo(root);
    }
}
